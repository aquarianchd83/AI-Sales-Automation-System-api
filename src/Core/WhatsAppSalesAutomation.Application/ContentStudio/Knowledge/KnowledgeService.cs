using System.Text;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;

/// <summary>
/// How ready the tenant's knowledge is, the follow-up questions that would improve it, and a check of claims against what has been verified.
/// Every method refuses unless the studio is on for the tenant, and reads and writes only that tenant's rows.
/// </summary>
public interface IKnowledgeService
{
    Task<KnowledgeReadinessDto> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Records the tenant's answer to a follow-up as a new (unverified) fact - or, for a gap that only lacks proof, attaches the proof to
    /// the facts involved - and returns the recalculated readiness.</summary>
    Task<KnowledgeReadinessDto> AnswerGapAsync(Guid tenantId, Guid? actorUserId, string requirementKey, AnswerGapRequest request, CancellationToken cancellationToken = default);

    /// <summary>"We don't need this." Refused for a requirement the rules do not allow skipping.</summary>
    Task<KnowledgeReadinessDto> SkipGapAsync(Guid tenantId, Guid? actorUserId, string requirementKey, CancellationToken cancellationToken = default);

    /// <summary>"Ask me later." The gap is hidden until the date (default a week, at most 90 days), then comes back.</summary>
    Task<KnowledgeReadinessDto> DeferGapAsync(Guid tenantId, Guid? actorUserId, string requirementKey, DeferGapRequest request, CancellationToken cancellationToken = default);

    Task<KnowledgeReadinessDto> ReopenGapAsync(Guid tenantId, string requirementKey, CancellationToken cancellationToken = default);

    /// <summary>Checks each sentence against the tenant's verified facts and says which are backed and which are not.</summary>
    Task<ClaimCheckResultDto> CheckClaimsAsync(Guid tenantId, CheckClaimsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The AI side's door to the tenant's knowledge. Returns only facts a person has verified and that are still current, only for the tenant
/// asked about, ranked by relevance to the question, with a ready-made block to put in a prompt so the model is told to say nothing else.
/// Every AI feature that talks about a tenant's business goes through here instead of reading facts itself.
/// </summary>
public interface IKnowledgeRetrievalService
{
    Task<GroundingResultDto> RetrieveAsync(Guid tenantId, string? query, int limit = 12, CancellationToken cancellationToken = default);
}

public class KnowledgeService : IKnowledgeService, IKnowledgeRetrievalService
{
    public const int MaxClaims = 50;
    public const int MaxClaimLength = 500;
    public const int DefaultDeferDays = 7;
    public const int MaxDeferDays = 90;

    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly IDateTimeProvider _dateTime;

    public KnowledgeService(IApplicationDbContext context, ITenantConfigOverrideProvider config, IDateTimeProvider dateTime)
    {
        _context = context;
        _config = config;
        _dateTime = dateTime;
    }

    // ----- readiness -------------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeReadinessDto> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var options = await EnsureEnabledAsync(tenantId, cancellationToken);
        var (rules, warning) = ReadinessRules.Resolve(options.ReadinessRules);
        return await EvaluateAsync(tenantId, rules, warning, cancellationToken);
    }

    private async Task<KnowledgeReadinessDto> EvaluateAsync(Guid tenantId, ReadinessRuleSet rules, string? warning, CancellationToken cancellationToken)
    {
        var facts = await _context.BusinessFacts.IgnoreQueryFilters().AsNoTracking().Where(f => f.TenantId == tenantId).ToListAsync(cancellationToken);
        var decisions = await _context.KnowledgeGapDecisions.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenantId).ToListAsync(cancellationToken);

        return KnowledgeReadinessEngine.Evaluate(new KnowledgeReadinessEngine.Inputs(
            rules, facts, decisions, await SpecialAnswersAsync(tenantId, cancellationToken), _dateTime.UtcNow, warning));
    }

    /// <summary>The questions the tenant answered "I don't know" / "I need help" / "Not applicable" to in its current questionnaire.</summary>
    private async Task<IReadOnlyDictionary<string, string>> SpecialAnswersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var response = await _context.QuestionnaireResponses.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (response is null)
            return new Dictionary<string, string>();

        var json = await _context.QuestionnaireTemplateVersions.Where(v => v.Id == response.VersionId).Select(v => v.DefinitionJson).FirstOrDefaultAsync(cancellationToken);
        var (definition, _) = json is null ? (null, null) : QuestionnaireEngine.TryParse(json);
        if (definition is null)
            return new Dictionary<string, string>();

        return QuestionnaireEngine.SpecialsFrom(definition, QuestionnaireEngine.ReadAnswers(response.AnswersJson)).ToDictionary(s => s.Key, s => s.Kind, StringComparer.Ordinal);
    }

    // ----- the follow-up workflow ------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeReadinessDto> AnswerGapAsync(
        Guid tenantId, Guid? actorUserId, string requirementKey, AnswerGapRequest request, CancellationToken cancellationToken = default)
    {
        var (rules, warning, def) = await ResolveAsync(tenantId, requirementKey, cancellationToken);
        var answer = BusinessFactService.Clean(request.Answer);
        var evidence = BusinessFactService.Clean(request.Evidence);

        var failures = new List<ValidationFailure>();
        if (answer is null && evidence is null)
            failures.Add(new ValidationFailure(nameof(request.Answer), "Write your answer."));
        if (def.RequiresEvidence && evidence is null)
            failures.Add(new ValidationFailure(nameof(request.Evidence), "This kind of claim needs something to back it up. Say how you know it is true."));
        if (answer is { Length: > BusinessFactService.MaxStatement })
            failures.Add(new ValidationFailure(nameof(request.Answer), $"Keep your answer to {BusinessFactService.MaxStatement} characters."));
        if (answer is not null && QuestionnaireEngine.LooksLikeSecret(answer))
            failures.Add(new ValidationFailure(nameof(request.Answer), QuestionnaireEngine.SecretMessage));
        if (BusinessFactService.Clean(request.Context) is { } ctx && (ctx.Length > BusinessFactService.MaxContext || QuestionnaireEngine.LooksLikeSecret(ctx)))
            failures.Add(new ValidationFailure(nameof(request.Context), ctx.Length > BusinessFactService.MaxContext ? $"Keep this to {BusinessFactService.MaxContext} characters." : QuestionnaireEngine.SecretMessage));
        if (evidence is not null)
            failures.AddRange(EvidenceProblems(evidence));
        if (failures.Count > 0)
            throw new ValidationException(failures);

        var now = _dateTime.UtcNow;
        if (answer is null)
        {
            // Only proof was given: attach it to the facts the tenant already told us, rather than inventing a second copy of the claim.
            var gap = (await EvaluateAsync(tenantId, rules, warning, cancellationToken)).Gaps.FirstOrDefault(g => g.Key == requirementKey);
            var ids = (gap?.FactsInvolved ?? Array.Empty<Guid>()).ToList();
            if (ids.Count == 0)
                throw new ValidationException(new[] { new ValidationFailure(nameof(request.Answer), "Write your answer - there is nothing yet for this proof to belong to.") });

            var facts = await _context.BusinessFacts.IgnoreQueryFilters().Where(f => f.TenantId == tenantId && ids.Contains(f.Id)).ToListAsync(cancellationToken);
            foreach (var fact in facts.Where(f => string.IsNullOrWhiteSpace(f.Evidence)))
            {
                fact.Evidence = evidence;
                fact.UpdatedBy = actorUserId;
                await BusinessFactHistory.RecordAsync(_context, fact, BusinessFactChangeKind.DetailsChanged, "Proof added.", actorUserId, false, cancellationToken);
            }
        }
        else
        {
            var fact = new BusinessFact
            {
                TenantId = tenantId,
                Category = def.Categories.FirstOrDefault() ?? "General",
                Subject = Truncate(def.Label, BusinessFactService.MaxSubject),
                Context = BusinessFactService.Clean(request.Context),
                Evidence = evidence,
                Statement = answer,
                SourceType = BusinessFactSource.FollowUp,
                SourceRef = def.Key.Length <= 60 ? def.Key : def.Key[..60],
                Origin = BusinessFactOrigin.TenantProvided,
                Status = BusinessFactStatus.Draft,
                UpdatedBy = actorUserId
            };
            _context.BusinessFacts.Add(fact);
            await BusinessFactHistory.RecordAsync(_context, fact, BusinessFactChangeKind.Created, "Your answer to a follow-up question.", actorUserId, true, cancellationToken);
        }

        await ClearDecisionAsync(tenantId, requirementKey, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return await EvaluateAsync(tenantId, rules, warning, cancellationToken);
    }

    public async Task<KnowledgeReadinessDto> SkipGapAsync(Guid tenantId, Guid? actorUserId, string requirementKey, CancellationToken cancellationToken = default)
    {
        var (rules, warning, def) = await ResolveAsync(tenantId, requirementKey, cancellationToken);
        if (!def.Skippable)
            throw new ConflictException("This one can't be skipped: the content it supports can't be made safely without it. You can ask to be reminded later instead.");

        await SetDecisionAsync(tenantId, requirementKey, KnowledgeGapDecisionKind.Skipped, null, actorUserId, cancellationToken);
        return await EvaluateAsync(tenantId, rules, warning, cancellationToken);
    }

    public async Task<KnowledgeReadinessDto> DeferGapAsync(
        Guid tenantId, Guid? actorUserId, string requirementKey, DeferGapRequest request, CancellationToken cancellationToken = default)
    {
        var (rules, warning, _) = await ResolveAsync(tenantId, requirementKey, cancellationToken);
        var now = _dateTime.UtcNow;
        var until = request.Until ?? now.AddDays(DefaultDeferDays);
        if (until <= now || until > now.AddDays(MaxDeferDays))
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Until), $"Choose a date within the next {MaxDeferDays} days.") });

        await SetDecisionAsync(tenantId, requirementKey, KnowledgeGapDecisionKind.Deferred, until, actorUserId, cancellationToken);
        return await EvaluateAsync(tenantId, rules, warning, cancellationToken);
    }

    public async Task<KnowledgeReadinessDto> ReopenGapAsync(Guid tenantId, string requirementKey, CancellationToken cancellationToken = default)
    {
        var (rules, warning, _) = await ResolveAsync(tenantId, requirementKey, cancellationToken);
        await ClearDecisionAsync(tenantId, requirementKey, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return await EvaluateAsync(tenantId, rules, warning, cancellationToken);
    }

    private async Task<(ReadinessRuleSet Rules, string? Warning, RequirementDef Def)> ResolveAsync(Guid tenantId, string requirementKey, CancellationToken cancellationToken)
    {
        var options = await EnsureEnabledAsync(tenantId, cancellationToken);
        var (rules, warning) = ReadinessRules.Resolve(options.ReadinessRules);
        var def = rules.Requirements.FirstOrDefault(r => r.Key == requirementKey)
                  ?? throw new NotFoundException("There is no such question to answer.");
        return (rules, warning, def);
    }

    private async Task SetDecisionAsync(Guid tenantId, string key, KnowledgeGapDecisionKind kind, DateTime? until, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var existing = await _context.KnowledgeGapDecisions.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.TenantId == tenantId && d.RequirementKey == key, cancellationToken);
        if (existing is null)
        {
            existing = new KnowledgeGapDecision { TenantId = tenantId, RequirementKey = key };
            _context.KnowledgeGapDecisions.Add(existing);
        }

        existing.Kind = kind;
        existing.DeferUntilUtc = until;
        existing.DecidedBy = actorUserId;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearDecisionAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var existing = await _context.KnowledgeGapDecisions.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.TenantId == tenantId && d.RequirementKey == key, cancellationToken);
        if (existing is not null)
            _context.KnowledgeGapDecisions.Remove(existing);
    }

    private static IEnumerable<ValidationFailure> EvidenceProblems(string evidence)
    {
        if (evidence.Length > BusinessFactService.MaxEvidence)
            yield return new ValidationFailure("Evidence", $"Keep this to {BusinessFactService.MaxEvidence} characters.");
        else if (QuestionnaireEngine.LooksLikeSecret(evidence))
            yield return new ValidationFailure("Evidence", QuestionnaireEngine.SecretMessage);
    }

    // ----- claims ----------------------------------------------------------------------------------------------------------------------

    public async Task<ClaimCheckResultDto> CheckClaimsAsync(Guid tenantId, CheckClaimsRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(tenantId, cancellationToken);

        var claims = (request.Claims ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (claims.Count == 0 || claims.Count > MaxClaims || claims.Any(c => c.Length > MaxClaimLength))
            throw new ValidationException(new[] { new ValidationFailure(nameof(request.Claims), $"Send between 1 and {MaxClaims} claims of up to {MaxClaimLength} characters each.") });

        var facts = await _context.BusinessFacts.IgnoreQueryFilters().AsNoTracking().Where(f => f.TenantId == tenantId).ToListAsync(cancellationToken);
        return KnowledgeClaimChecker.Check(claims, facts, _dateTime.UtcNow);
    }

    // ----- retrieval for the AI side ---------------------------------------------------------------------------------------------------

    public async Task<GroundingResultDto> RetrieveAsync(Guid tenantId, string? query, int limit = 12, CancellationToken cancellationToken = default)
    {
        await EnsureEnabledAsync(tenantId, cancellationToken);
        limit = Math.Clamp(limit, 1, 50);
        var now = _dateTime.UtcNow;

        // The tenant id is part of the query itself, not just of the ambient filter: this runs from background work as well as from requests.
        var verified = await _context.BusinessFacts.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.Status == BusinessFactStatus.Verified && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now))
            .ToListAsync(cancellationToken);

        var terms = Terms(query);
        var ranked = verified
            .Select(f => (Fact: f, Score: terms.Count == 0 ? 0.0 : Relevance(f, terms)))
            .Where(x => terms.Count == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Fact.Category, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Fact.CreatedAt)
            .Take(limit)
            .ToList();

        var facts = ranked.Select(x => new GroundingFactDto(x.Fact.Id, x.Fact.Category, x.Fact.Subject, x.Fact.Statement, x.Fact.Context, Math.Round(x.Score, 3))).ToList();
        return new GroundingResultDto(facts, BuildPromptBlock(facts));
    }

    private static string BuildPromptBlock(IReadOnlyList<GroundingFactDto> facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("These are the only facts about this business that have been verified. State nothing about the business that they do not say; if something is not here, do not claim it. Do not state a figure, price, result or guarantee unless it appears below.");
        if (facts.Count == 0)
        {
            sb.AppendLine("(No verified facts are available for this request.)");
            return sb.ToString().TrimEnd();
        }

        for (var i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            sb.Append('[').Append(i + 1).Append("] ").Append(f.Category);
            if (!string.IsNullOrWhiteSpace(f.Subject)) sb.Append(" / ").Append(f.Subject);
            sb.Append(": ").Append(f.Statement);
            if (!string.IsNullOrWhiteSpace(f.Context)) sb.Append(" (").Append(f.Context).Append(')');
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static HashSet<string> Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? new HashSet<string>()
            : query.ToLowerInvariant().Split(new[] { ' ', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '-', '/', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2).Select(w => w.TrimEnd('s')).ToHashSet();

    private static double Relevance(BusinessFact fact, IReadOnlySet<string> terms)
    {
        var text = string.Join(' ', fact.Category, fact.Subject, fact.Statement, fact.Context).ToLowerInvariant();
        var words = text.Split(new[] { ' ', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '-', '/', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd('s')).ToHashSet();
        var hits = terms.Count(words.Contains);
        // A match in the category or subject says more about what the fact is than a match somewhere in its sentence.
        var headline = string.Join(' ', fact.Category, fact.Subject).ToLowerInvariant();
        var headlineHits = terms.Count(t => headline.Contains(t, StringComparison.Ordinal));
        return terms.Count == 0 ? 0 : (hits + 0.5 * headlineHits) / terms.Count;
    }

    private async Task<StudioOptions> EnsureEnabledAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var options = await _config.GetStudioOptionsForTenantAsync(tenantId, cancellationToken);
        if (!options.Enabled)
            throw new FeatureDisabledException("The AI Content Studio is not switched on for your workspace.");
        return options;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
