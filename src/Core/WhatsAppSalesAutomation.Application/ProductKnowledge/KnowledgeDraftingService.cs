using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using ValidationFailure = FluentValidation.Results.ValidationFailure;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>
/// Drafts answers to the product questions from the tenant's own sources, and shows its working.
///
/// For each question: find the passages that bear on it (and stop there if none do - nothing is asked of the AI, nothing is invented), ask
/// the chosen provider for a structured answer that must quote its passages, check every quotation against the passage's real text and drop
/// those that are not there, check every figure in the answer against the evidence, look for sources that disagree, and only then work out
/// the confidence - from what was verified, never from what the model said about itself. An answer the evidence does not support is not
/// shown as an answer at all: the tenant sees the limitation and one question to close the gap.
///
/// The AI cost is the platform's; the tenant is not charged. An answer the tenant has approved is never replaced by a new draft.
/// </summary>
public sealed class KnowledgeDraftingService : IKnowledgeDraftingService, IKnowledgeDraftRunner
{
    private const int MaxPassagesLoaded = 4000;
    private static readonly TimeSpan StaleRun = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _db;
    private readonly IAiTaskRunner _ai;
    private readonly IAiFeatureRouting _routing;
    private readonly ISourceLimitsProvider _limits;
    private readonly IKnowledgeDraftQueue _queue;
    private readonly ILogger<KnowledgeDraftingService> _logger;

    public KnowledgeDraftingService(
        IApplicationDbContext db, IAiTaskRunner ai, IAiFeatureRouting routing, ISourceLimitsProvider limits, IKnowledgeDraftQueue queue,
        ILogger<KnowledgeDraftingService> logger)
    {
        _db = db;
        _ai = ai;
        _routing = routing;
        _limits = limits;
        _queue = queue;
        _logger = logger;
    }

    private enum Outcome { Drafted, NoEvidence, Skipped, Failed }

    private sealed record DraftResult(Outcome Outcome, KnowledgeAnswer? Answer, string? Failure);

    // ----- reading ---------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeAnswersOverviewDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await EnsureStandardQuestionsAsync(tenantId, cancellationToken);

        var questions = await _db.KnowledgeQuestions.AsNoTracking().Where(q => q.TenantId == tenantId).OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt).ToListAsync(cancellationToken);
        var answers = await _db.KnowledgeAnswers.AsNoTracking().Where(a => a.TenantId == tenantId && a.IsCurrent).ToListAsync(cancellationToken);
        var answerIds = answers.Select(a => a.Id).ToList();
        var evidence = await _db.AnswerEvidences.AsNoTracking().Where(e => e.TenantId == tenantId && answerIds.Contains(e.AnswerId)).ToListAsync(cancellationToken);
        var conflicts = await _db.KnowledgeConflicts.AsNoTracking().Where(c => c.TenantId == tenantId && c.AnswerId != null && answerIds.Contains(c.AnswerId.Value)).ToListAsync(cancellationToken);

        var dtos = questions.Select(q =>
        {
            var a = answers.FirstOrDefault(x => x.QuestionId == q.Id);
            return new KnowledgeQuestionDto(q.Id, q.Key, q.Text, q.IsStandard, q.Category, q.SortOrder,
                a is null ? null : ToDto(a, evidence.Where(e => e.AnswerId == a.Id), conflicts.Where(c => c.AnswerId == a.Id)));
        }).ToList();

        var features = await _db.FeatureBenefits.AsNoTracking().Where(f => f.TenantId == tenantId).OrderBy(f => f.CreatedAt).ToListAsync(cancellationToken);
        var run = await _db.KnowledgeDraftRuns.AsNoTracking().Where(r => r.TenantId == tenantId).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var (sources, passages) = await CountsAsync(tenantId, cancellationToken);

        var route = _routing.Resolve(AiFeatures.ProductSuggestedAnswers);
        var limits = _limits.Get();
        return new KnowledgeAnswersOverviewDto(
            dtos, features.Select(ToDto).ToList(), run is null ? null : ToDto(run), sources, passages,
            route.IsOff ? AiFeatures.OffMode : route.UsesThirdParty ? PlatformIntegrationSettingsService.ApiMode : PlatformIntegrationSettingsService.SimulatorMode,
            route.UsesThirdParty ? route.Provider : AiCapabilities.Simulator, limits.MaxCustomQuestions, questions.Count(q => !q.IsStandard), ComputeProgress(dtos));
    }

    public async Task<DraftRunDto> GetRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.KnowledgeDraftRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException("KnowledgeDraftRun", runId);
        return ToDto(run);
    }

    // ----- starting work ---------------------------------------------------------------------------------------------------------

    public async Task<DraftRunDto> StartRunAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken = default)
    {
        EnsureNotOff();

        var (sources, _) = await CountsAsync(tenantId, cancellationToken);
        if (sources == 0)
            throw new ConflictException("Add a document or your website and let it finish reading first - there is nothing to draft answers from yet.");

        var recent = DateTime.UtcNow - StaleRun;
        var active = await _db.KnowledgeDraftRuns
            .Where(r => r.TenantId == tenantId && (r.Status == KnowledgeDraftRunStatus.Queued || r.Status == KnowledgeDraftRunStatus.Running) && r.CreatedAt > recent)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (active is not null)
            return ToDto(active);

        await EnsureStandardQuestionsAsync(tenantId, cancellationToken);
        var run = new KnowledgeDraftRun { TenantId = tenantId, CreatedBy = userId, Status = KnowledgeDraftRunStatus.Queued };
        _db.KnowledgeDraftRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        _queue.Enqueue(run.Id, tenantId);
        return ToDto(run);
    }

    public async Task<KnowledgeQuestionDto> RedraftAsync(Guid tenantId, Guid questionId, CancellationToken cancellationToken = default)
    {
        EnsureNotOff();
        var question = await FindQuestionAsync(tenantId, questionId, cancellationToken);
        var candidates = await LoadCandidatesAsync(tenantId, cancellationToken);

        var result = await DraftOneAsync(tenantId, question, candidates, cancellationToken, explicitRequest: true);
        if (result.Outcome == Outcome.Skipped)
            throw new ConflictException("This answer is already approved, so it is not replaced. Reject or correct it first to ask for another suggestion.");
        if (result.Outcome == Outcome.Failed)
            throw new ConflictException($"The AI could not answer this just now: {result.Failure}");

        return await ToQuestionDtoAsync(tenantId, question, cancellationToken);
    }

    public async Task<KnowledgeQuestionDto> AskAsync(Guid tenantId, Guid? userId, AskQuestionRequest request, CancellationToken cancellationToken = default)
    {
        var text = Regex.Replace(request?.Text ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length < 8)
            throw Invalid("text", "Write your question in a full sentence.");
        if (text.Length > 500)
            throw Invalid("text", "Keep the question under 500 characters.");
        EnsureNotOff();

        await EnsureStandardQuestionsAsync(tenantId, cancellationToken);
        var custom = await _db.KnowledgeQuestions.CountAsync(q => q.TenantId == tenantId && !q.IsStandard, cancellationToken);
        var limit = _limits.Get().MaxCustomQuestions;
        if (custom >= limit)
            throw Invalid("text", $"You have {custom} questions of your own, the most allowed. Remove one to add another.");
        if (await _db.KnowledgeQuestions.AnyAsync(q => q.TenantId == tenantId && !q.IsStandard && q.Text == text, cancellationToken))
            throw new ConflictException("You have already asked this question.");

        var question = new KnowledgeQuestion
        {
            TenantId = tenantId, Text = text, IsStandard = false, Category = "Custom", SortOrder = 1000 + custom, CreatedBy = userId,
            ClarifyingQuestion = "What is the answer to this, in your own words? Add it so it is available to answer customers."
        };
        _db.KnowledgeQuestions.Add(question);
        await _db.SaveChangesAsync(cancellationToken);

        var candidates = await LoadCandidatesAsync(tenantId, cancellationToken);
        var result = await DraftOneAsync(tenantId, question, candidates, cancellationToken);
        if (result.Outcome == Outcome.Failed)
        {
            // A question with no answer and no way to tell why would just sit there: take it back, so the text can be sent again.
            question.IsDeleted = true;
            question.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            throw new ConflictException($"The AI could not answer this just now: {result.Failure}");
        }

        return await ToQuestionDtoAsync(tenantId, question, cancellationToken);
    }

    public async Task DeleteQuestionAsync(Guid tenantId, Guid questionId, CancellationToken cancellationToken = default)
    {
        var question = await FindQuestionAsync(tenantId, questionId, cancellationToken);
        if (question.IsStandard)
            throw new ConflictException("The standard questions cannot be removed. You can leave them unanswered.");

        question.IsDeleted = true;
        question.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ----- the run ---------------------------------------------------------------------------------------------------------------

    public async Task RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.KnowledgeDraftRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.Status != KnowledgeDraftRunStatus.Queued)
            return;

        run.Status = KnowledgeDraftRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            if (_routing.Resolve(AiFeatures.ProductSuggestedAnswers).IsOff)
            {
                FinishFailed(run, "Suggested answers were switched off on this platform before the run could start.");
                await _db.SaveChangesAsync(CancellationToken.None);
                return;
            }

            await EnsureStandardQuestionsAsync(run.TenantId, cancellationToken);
            var questions = await _db.KnowledgeQuestions.Where(q => q.TenantId == run.TenantId).OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt).ToListAsync(cancellationToken);
            var candidates = await LoadCandidatesAsync(run.TenantId, cancellationToken);
            run.Total = questions.Count + 1; // every question, then the feature-to-benefit map
            await _db.SaveChangesAsync(cancellationToken);

            foreach (var question in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DraftResult result;
                try
                {
                    result = await DraftOneAsync(run.TenantId, question, candidates, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Drafting an answer for question {QuestionId} failed", question.Id);
                    result = new DraftResult(Outcome.Failed, null, "Something went wrong while drafting this answer.");
                }

                run.Done++;
                switch (result.Outcome)
                {
                    case Outcome.Drafted: run.Drafted++; break;
                    case Outcome.NoEvidence: run.NoEvidence++; break;
                    case Outcome.Skipped: run.Skipped++; break;
                    default:
                        run.Failed++;
                        run.Error ??= result.Failure;
                        break;
                }
                await _db.SaveChangesAsync(cancellationToken);
            }

            try
            {
                await MapFeaturesAsync(run.TenantId, candidates, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Mapping features to benefits failed for tenant {TenantId}", run.TenantId);
                run.Error ??= "The feature-to-benefit list could not be drafted.";
            }

            run.Done++;
            run.Status = KnowledgeDraftRunStatus.Completed;
            run.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Draft run {RunId} failed", runId);
            FinishFailed(run, "Something went wrong while drafting. Try again.");
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static void FinishFailed(KnowledgeDraftRun run, string message)
    {
        run.Status = KnowledgeDraftRunStatus.Failed;
        run.Error = message;
        run.FinishedAt = DateTime.UtcNow;
    }

    // ----- one answer ------------------------------------------------------------------------------------------------------------

    private async Task<DraftResult> DraftOneAsync(
        Guid tenantId, KnowledgeQuestion question, IReadOnlyList<PassageCandidate> all, CancellationToken ct, bool explicitRequest = false)
    {
        var previous = await _db.KnowledgeAnswers.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.QuestionId == question.Id && a.IsCurrent, ct);

        // An approved answer is never replaced; a question the tenant called "not applicable" is left alone by a run (they can still ask for a suggestion).
        if (previous is { Status: KnowledgeVerificationStatus.VerifiedAndApproved } || (!explicitRequest && previous is { Status: KnowledgeVerificationStatus.NotApplicable }))
            return new DraftResult(Outcome.Skipped, previous, null);

        var hints = StandardQuestions.All.FirstOrDefault(s => s.Key == question.Key)?.Hints;
        var offered = PassageRetriever.Rank(question.Text, hints, all, _limits.Get().DraftPassages).Select(r => r.Passage).ToList();

        KnowledgeAnswer answer;
        if (offered.Count == 0)
        {
            // Nothing in the sources bears on the question: say so without asking the AI anything.
            answer = NewAnswer(tenantId, question, previous);
            answer.Confidence = KnowledgeConfidence.NoEvidence;
            answer.Provider = "None";
            answer.Limitation = all.Count == 0
                ? "There is nothing to search yet: none of your sources has been read."
                : "None of your sources mention this.";
            answer.ClarifyingQuestion = question.ClarifyingQuestion;
            answer.ConfidenceReasons = "No source passage is relevant to this question.";
            await SaveAnswerAsync(previous, answer, Array.Empty<AnswerEvidence>(), Array.Empty<DetectedConflict>(), ct);
            return new DraftResult(Outcome.NoEvidence, answer, null);
        }

        var task = question.IsStandard ? AiTaskKinds.SuggestedAnswerGeneration : AiTaskKinds.CustomQuestionAnswering;
        var call = await _ai.RunAsync(new AiTaskRequest(task, DraftPrompts.AnswerSystem, DraftPrompts.AnswerUser(question.Text, offered), 900, ExpectJson: true, TenantId: tenantId), ct);
        if (!call.Success)
            return new DraftResult(Outcome.Failed, null, call.ErrorMessage ?? "the AI provider gave no answer");

        var reply = DraftPrompts.ParseAnswer(call.Text);
        if (reply is null)
            return new DraftResult(Outcome.Failed, null, "the AI's reply could not be read");

        // Nothing the model says is believed until it is checked: quotations against the real passages...
        var (verified, dropped) = VerifyEvidence(reply.Evidence, offered, call.IsSimulated);
        var answerText = reply.InsufficientEvidence ? null : reply.Answer;
        var cited = verified.Select(v => v.Passage).DistinctBy(p => p.Id).ToList();

        // ...figures against the cited passages, and sources against each other.
        var unsupported = answerText is null || verified.Count == 0
            ? new List<string>()
            : ClaimRules.Unsupported($"{answerText} {reply.Detail}", cited.Select(p => p.Text));
        var conflicts = verified.Count == 0 ? new List<DetectedConflict>() : ConflictDetector.Detect(offered);

        var assessed = ConfidenceEngine.Assess(new ConfidenceInput(
            answerText, verified.Select(v => new EvidenceSnippet(v.Passage.Id, v.Excerpt, v.Passage.Text)).ToList(), reply.Inference, reply.InsufficientEvidence,
            conflicts.Count > 0, unsupported.Count > 0, call.IsSimulated));

        answer = NewAnswer(tenantId, question, previous);
        answer.Provider = call.Provider;
        answer.Model = call.Model;
        answer.IsSimulated = call.IsSimulated;
        answer.AiCorrelationId = call.CorrelationId;
        answer.Confidence = assessed.Confidence;
        answer.Kind = assessed.Kind;
        answer.ConfidenceReasons = Truncate(string.Join('\n', assessed.Reasons), 1500);
        answer.HasConflict = conflicts.Count > 0;
        answer.HasUnsupportedClaim = unsupported.Count > 0;
        answer.ClarifyingQuestion = Truncate(reply.ClarifyingQuestion ?? question.ClarifyingQuestion, 500);

        if (assessed.Confidence == KnowledgeConfidence.NoEvidence)
        {
            // Not shown as an answer: what the AI wrote is discarded, and the tenant sees why.
            answer.Limitation = Truncate(call.IsSimulated
                ? "Simulated test data: no source was read and no AI provider was contacted. This is not an answer."
                : reply.Answer is not null && !reply.InsufficientEvidence
                    ? "The AI suggested an answer, but none of its quotations could be found in your sources, so it was discarded."
                    : reply.Limitations ?? "Your sources do not answer this.", 2000);
        }
        else
        {
            answer.Summary = Truncate(answerText, 2000);
            answer.Detail = Truncate(reply.Detail, 8000);
            answer.IsMaterialClaim = ClaimRules.IsMaterial($"{answer.Summary} {answer.Detail}");
            answer.Limitation = Truncate(Limitations(reply, unsupported, dropped, conflicts), 2000);
        }

        var rows = verified.Select(v => new AnswerEvidence
        {
            TenantId = tenantId, SourceId = v.Passage.SourceId, PassageId = v.Passage.Id, SourceVersion = v.Passage.SourceVersion, Excerpt = Truncate(v.Excerpt, 2000)!,
            SourceName = Truncate(v.Passage.SourceName, 2048)!, Location = Truncate(v.Passage.Location, 2048)!, Heading = Truncate(v.Passage.Heading, 300), PassageHash = v.Passage.Hash
        }).ToList();
        answer.SourceVersions = JsonSerializer.Serialize(cited.Select(p => new { sourceId = p.SourceId, version = p.SourceVersion }).Distinct().ToList(), Json);

        await SaveAnswerAsync(previous, answer, assessed.Confidence == KnowledgeConfidence.NoEvidence ? Array.Empty<AnswerEvidence>() : rows, conflicts, ct);
        return new DraftResult(assessed.Confidence == KnowledgeConfidence.NoEvidence ? Outcome.NoEvidence : Outcome.Drafted, answer, null);
    }

    private static KnowledgeAnswer NewAnswer(Guid tenantId, KnowledgeQuestion question, KnowledgeAnswer? previous) => new()
    {
        TenantId = tenantId, QuestionId = question.Id, Version = (previous?.Version ?? 0) + 1, IsCurrent = true, GeneratedAt = DateTime.UtcNow,
        Status = KnowledgeVerificationStatus.PendingVerification, Kind = KnowledgeAnswerKind.AiInterpreted, Provider = "None"
    };

    private async Task SaveAnswerAsync(
        KnowledgeAnswer? previous, KnowledgeAnswer answer, IReadOnlyList<AnswerEvidence> evidence, IReadOnlyList<DetectedConflict> conflicts, CancellationToken ct)
    {
        if (previous is not null)
            previous.IsCurrent = false; // kept as an earlier suggestion; the new one is current

        _db.KnowledgeAnswers.Add(answer);
        foreach (var row in evidence)
        {
            row.AnswerId = answer.Id;
            _db.AnswerEvidences.Add(row);
        }

        foreach (var conflict in conflicts.Take(3))
        {
            _db.KnowledgeConflicts.Add(new KnowledgeConflict
            {
                TenantId = answer.TenantId, AnswerId = answer.Id, Fact = conflict.Fact, ValuesJson = JsonSerializer.Serialize(conflict.Values, Json), DetectedAt = DateTime.UtcNow
            });
        }

        _db.KnowledgeAnswerRevisions.Add(new KnowledgeAnswerRevision
        {
            TenantId = answer.TenantId, QuestionId = answer.QuestionId, AnswerId = answer.Id, Kind = KnowledgeRevisionKind.Drafted,
            FromStatus = previous?.Status.ToString(), ToStatus = answer.Status.ToString(), FromText = Truncate(previous?.Summary, 2000), ToText = Truncate(answer.Summary, 2000),
            Note = Truncate(answer.IsSimulated ? "Test data from the simulator." : $"Drafted by {answer.Provider}: {answer.Confidence} confidence, {evidence.Count} verified quotation{(evidence.Count == 1 ? string.Empty : "s")}.", 1000)
        });

        await _db.SaveChangesAsync(ct);
    }

    private static string? Limitations(DraftReply reply, IReadOnlyList<string> unsupported, int dropped, IReadOnlyList<DetectedConflict> conflicts)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(reply.Limitations))
            notes.Add(reply.Limitations!);
        if (unsupported.Count > 0)
            notes.Add($"Not stated in the cited passages, so unproven: {string.Join(", ", unsupported)}.");
        if (conflicts.Count > 0)
            notes.Add($"Your sources disagree about the {string.Join(" and the ", conflicts.Select(c => c.Fact).Distinct())}. Check which is right.");
        if (dropped > 0)
            notes.Add($"{dropped} quotation{(dropped == 1 ? string.Empty : "s")} could not be found in your sources and {(dropped == 1 ? "was" : "were")} not used.");
        return notes.Count == 0 ? null : string.Join(' ', notes);
    }

    /// <summary>Keeps only the quotations that are really in the passage they name, cut from the passage's own words.</summary>
    private static (List<(PassageCandidate Passage, string Excerpt)> Verified, int Dropped) VerifyEvidence(
        IReadOnlyList<DraftEvidenceRef> refs, IReadOnlyList<PassageCandidate> offered, bool simulated)
    {
        var verified = new List<(PassageCandidate, string)>();
        var dropped = 0;
        if (simulated)
            return (verified, 0); // the simulator reads nothing, so it has no evidence to offer

        foreach (var r in refs)
        {
            var m = Regex.Match(r.Passage, @"^\s*\[?P(\d+)\]?\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > offered.Count)
            {
                dropped++;
                continue;
            }

            var passage = offered[n - 1];
            var excerpt = QuoteVerifier.Locate(passage.Text, r.Quote);
            if (excerpt is null)
            {
                dropped++;
                continue;
            }

            if (!verified.Any(v => v.Item1.Id == passage.Id && v.Item2 == excerpt))
                verified.Add((passage, excerpt));
        }

        return (verified, dropped);
    }

    // ----- features and benefits -------------------------------------------------------------------------------------------------

    private async Task MapFeaturesAsync(Guid tenantId, IReadOnlyList<PassageCandidate> all, CancellationToken ct)
    {
        var offered = PassageRetriever.Rank(
            "features of the product and how they help customers", "feature features includes offers provides capability helps allows service services benefits",
            all, Math.Min(12, _limits.Get().DraftPassages * 2)).Select(r => r.Passage).ToList();
        if (offered.Count == 0)
            return;

        var call = await _ai.RunAsync(new AiTaskRequest(AiTaskKinds.BenefitAnalysis, DraftPrompts.FeatureSystem, DraftPrompts.FeatureUser(offered), 1800, ExpectJson: true, TenantId: tenantId), ct);
        // A failed or simulated call leaves the existing list alone: the simulator has no features to give and must not wipe real ones.
        if (!call.Success || call.IsSimulated)
            return;

        var replies = DraftPrompts.ParseFeatures(call.Text);
        if (replies is null)
            return;

        var rows = new List<FeatureBenefit>();
        foreach (var f in replies)
        {
            var (verified, _) = VerifyEvidence(f.Evidence, offered, simulated: false);
            if (verified.Count == 0)
                continue; // a feature no source passage supports is left out, not guessed

            var text = $"{f.Feature}. {f.Problem} {f.Benefit}";
            var cited = verified.Select(v => v.Passage).DistinctBy(p => p.Id).ToList();
            var unsupported = ClaimRules.Unsupported(text, cited.Select(p => p.Text));
            var assessed = ConfidenceEngine.Assess(new ConfidenceInput(
                text, verified.Select(v => new EvidenceSnippet(v.Passage.Id, v.Excerpt, v.Passage.Text)).ToList(), f.Inference, false, false, unsupported.Count > 0, false));

            rows.Add(new FeatureBenefit
            {
                TenantId = tenantId, Feature = Truncate(f.Feature, 300)!, Problem = Truncate(f.Problem, 1000), Benefit = Truncate(f.Benefit, 1500), Segment = Truncate(f.Segment, 300),
                Confidence = assessed.Confidence, HasUnsupportedClaim = unsupported.Count > 0, Provider = call.Provider, IsSimulated = false, GeneratedAt = DateTime.UtcNow,
                EvidenceJson = JsonSerializer.Serialize(verified.Select(v => new AnswerEvidenceDto(v.Passage.SourceId, v.Passage.SourceName, v.Passage.Location, v.Passage.Heading, v.Excerpt)).ToList(), Json)
            });
        }

        var old = await _db.FeatureBenefits.Where(f => f.TenantId == tenantId).ToListAsync(ct);
        var version = old.Count == 0 ? 1 : old.Max(f => f.RunVersion) + 1;
        _db.FeatureBenefits.RemoveRange(old);
        foreach (var row in rows)
        {
            row.RunVersion = version;
            _db.FeatureBenefits.Add(row);
        }
        await _db.SaveChangesAsync(ct);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private void EnsureNotOff()
    {
        if (_routing.Resolve(AiFeatures.ProductSuggestedAnswers).IsOff)
            throw new FeatureDisabledException("Suggested answers are switched off on this platform for now. Ask your platform administrator if you need them.");
    }

    private async Task EnsureStandardQuestionsAsync(Guid tenantId, CancellationToken ct)
    {
        var existing = await _db.KnowledgeQuestions.Where(q => q.TenantId == tenantId && q.IsStandard).Select(q => q.Key).ToListAsync(ct);
        var missing = StandardQuestions.All.Select((s, i) => (s, i)).Where(x => !existing.Contains(x.s.Key)).ToList();
        if (missing.Count == 0)
            return;

        foreach (var (s, i) in missing)
        {
            _db.KnowledgeQuestions.Add(new KnowledgeQuestion
            {
                TenantId = tenantId, Key = s.Key, Text = s.Text, IsStandard = true, Category = s.Category, ClarifyingQuestion = s.Clarifying, SortOrder = i
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<KnowledgeQuestion> FindQuestionAsync(Guid tenantId, Guid questionId, CancellationToken ct) =>
        await _db.KnowledgeQuestions.FirstOrDefaultAsync(q => q.Id == questionId && q.TenantId == tenantId, ct) ?? throw new NotFoundException("KnowledgeQuestion", questionId);

    private async Task<(int Sources, int Passages)> CountsAsync(Guid tenantId, CancellationToken ct)
    {
        var sources = await _db.KnowledgeSources.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status == KnowledgeSourceStatus.Ready).Select(s => new { s.Id, s.Version }).ToListAsync(ct);
        var ids = sources.Select(s => s.Id).ToList();
        var passages = await _db.SourcePassages.AsNoTracking().Where(p => p.TenantId == tenantId && ids.Contains(p.SourceId)).Select(p => new { p.SourceId, p.SourceVersion }).ToListAsync(ct);
        var versions = sources.ToDictionary(s => s.Id, s => s.Version);
        return (sources.Count, passages.Count(p => versions.TryGetValue(p.SourceId, out var v) && v == p.SourceVersion));
    }

    /// <summary>Every passage of the current version of every source that has been read - the tenant's own, never anyone else's.</summary>
    private async Task<IReadOnlyList<PassageCandidate>> LoadCandidatesAsync(Guid tenantId, CancellationToken ct)
    {
        var sources = await _db.KnowledgeSources.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status == KnowledgeSourceStatus.Ready).ToListAsync(ct);
        var ids = sources.Select(s => s.Id).ToList();
        var passages = await _db.SourcePassages.AsNoTracking().Where(p => p.TenantId == tenantId && ids.Contains(p.SourceId)).OrderBy(p => p.SourceId).ThenBy(p => p.Order)
            .Take(MaxPassagesLoaded).ToListAsync(ct);

        var list = new List<PassageCandidate>();
        foreach (var p in passages)
        {
            var source = sources.First(s => s.Id == p.SourceId);
            if (p.SourceVersion != source.Version)
                continue;
            var location = p.Url ?? (p.PageNumber is { } n ? $"Page {n}" : "Document");
            list.Add(new PassageCandidate(p.Id, source.Id, source.Name, source.Version, location, p.Heading, p.Text, p.Hash));
        }
        return list;
    }

    private Task<KnowledgeQuestionDto> ToQuestionDtoAsync(Guid tenantId, KnowledgeQuestion question, CancellationToken ct) => BuildQuestionDtoAsync(_db, tenantId, question, ct);

    internal static async Task<KnowledgeQuestionDto> BuildQuestionDtoAsync(IApplicationDbContext db, Guid tenantId, KnowledgeQuestion question, CancellationToken ct)
    {
        var answer = await db.KnowledgeAnswers.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.QuestionId == question.Id && a.IsCurrent, ct);
        if (answer is null)
            return new KnowledgeQuestionDto(question.Id, question.Key, question.Text, question.IsStandard, question.Category, question.SortOrder, null);

        var evidence = await db.AnswerEvidences.AsNoTracking().Where(e => e.TenantId == tenantId && e.AnswerId == answer.Id).ToListAsync(ct);
        var conflicts = await db.KnowledgeConflicts.AsNoTracking().Where(c => c.TenantId == tenantId && c.AnswerId == answer.Id).ToListAsync(ct);
        return new KnowledgeQuestionDto(question.Id, question.Key, question.Text, question.IsStandard, question.Category, question.SortOrder, ToDto(answer, evidence, conflicts));
    }

    internal static KnowledgeAnswerDto ToDto(KnowledgeAnswer a, IEnumerable<AnswerEvidence> evidence, IEnumerable<KnowledgeConflict> conflicts) => new(
        a.Id, a.Version, a.Summary, a.Detail, a.Kind.ToString(), a.Confidence.ToString(), a.Status.ToString(), a.Limitation, a.ClarifyingQuestion,
        string.IsNullOrEmpty(a.ConfidenceReasons) ? Array.Empty<string>() : a.ConfidenceReasons.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        a.IsMaterialClaim, a.HasUnsupportedClaim, a.HasConflict, a.Provider, a.Model, a.IsSimulated, a.GeneratedAt,
        evidence.Select(e => new AnswerEvidenceDto(e.SourceId, e.SourceName, e.Location, e.Heading, e.Excerpt)).ToList(),
        conflicts.Select(c => new ConflictDto(c.Id, c.Fact, ReadValues(c.ValuesJson), c.Status.ToString(), c.ChosenValue)).ToList(), a.ApprovedAt);

    /// <summary>Each question is in exactly one state. "Needs information" is no answer yet, or sources that do not answer it.</summary>
    internal static AnswerProgressDto ComputeProgress(IReadOnlyList<KnowledgeQuestionDto> questions)
    {
        int approved = 0, waiting = 0, needs = 0, rejected = 0, unknown = 0, notApplicable = 0;
        foreach (var q in questions)
        {
            switch (q.Answer?.Status)
            {
                case nameof(KnowledgeVerificationStatus.VerifiedAndApproved): approved++; break;
                case nameof(KnowledgeVerificationStatus.Rejected): rejected++; break;
                case nameof(KnowledgeVerificationStatus.InformationNotAvailable): unknown++; break;
                case nameof(KnowledgeVerificationStatus.NotApplicable): notApplicable++; break;
                case nameof(KnowledgeVerificationStatus.CorrectionRequired): waiting++; break;
                case null: needs++; break;
                default:
                    if (q.Answer!.Confidence == nameof(KnowledgeConfidence.NoEvidence))
                        needs++;
                    else
                        waiting++;
                    break;
            }
        }
        return new AnswerProgressDto(questions.Count, approved, waiting, needs, rejected, unknown, notApplicable);
    }

    private static IReadOnlyList<ConflictValue> ReadValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ConflictValue>>(json, Json) ?? new List<ConflictValue>();
        }
        catch (JsonException)
        {
            return Array.Empty<ConflictValue>();
        }
    }

    private static FeatureBenefitDto ToDto(FeatureBenefit f)
    {
        List<AnswerEvidenceDto> evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<List<AnswerEvidenceDto>>(f.EvidenceJson, Json) ?? new List<AnswerEvidenceDto>();
        }
        catch (JsonException)
        {
            evidence = new List<AnswerEvidenceDto>();
        }
        return new FeatureBenefitDto(f.Id, f.Feature, f.Problem, f.Benefit, f.Segment, f.Confidence.ToString(), f.HasUnsupportedClaim, evidence, f.Provider, f.IsSimulated, f.GeneratedAt);
    }

    private static DraftRunDto ToDto(KnowledgeDraftRun r) =>
        new(r.Id, r.Status.ToString(), r.Total, r.Done, r.Drafted, r.NoEvidence, r.Skipped, r.Failed, r.Error, r.CreatedAt, r.StartedAt, r.FinishedAt);

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
