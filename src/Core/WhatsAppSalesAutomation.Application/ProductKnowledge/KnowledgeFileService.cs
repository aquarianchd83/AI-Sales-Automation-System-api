using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <param name="FileName">What the browser saves it as.</param>
/// <param name="Content">UTF-8 text.</param>
/// <param name="StoredKey">Where a copy was kept in the tenant's private AWS folder, or null when AWS is not set up or the copy failed (the file is still returned).</param>
public record KnowledgeFileResult(string FileName, byte[] Content, string? StoredKey);

public interface IKnowledgeFileService
{
    /// <summary>The working file: every question with its answer, evidence and status, plainly headed as not approved.</summary>
    Task<KnowledgeFileResult> BuildDraftAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The record of one published version: the approved answers only, grouped by category, with where each came from.</summary>
    Task<KnowledgeFileResult> BuildFinalAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Keeps the Final file of a version in the tenant's AWS folder. Never fails the caller: the database is the knowledge, the file is a record.</summary>
    Task SaveFinalCopyAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Plain-text files of the tenant's product knowledge, for review and record keeping. The knowledge itself lives in the database; a file is
/// generated from it on request, so it is always current, and a copy is kept in the tenant's private AWS folder. The Final file is built from the
/// published items of a version and from nothing else, so a draft, an unapproved, a rejected, a conflicting or a test-data answer cannot appear
/// in it as fact. The Draft file is headed as not approved and says so again beside every answer.
/// </summary>
public sealed class KnowledgeFileService : IKnowledgeFileService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Rule = "------------------------------------------------------------";

    private readonly IApplicationDbContext _db;
    private readonly ITenantDocumentStore _store;
    private readonly ILogger<KnowledgeFileService> _logger;

    public KnowledgeFileService(IApplicationDbContext db, ITenantDocumentStore store, ILogger<KnowledgeFileService> logger)
    {
        _db = db;
        _store = store;
        _logger = logger;
    }

    // ----- Draft -----------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeFileResult> BuildDraftAsync(Guid tenantId, CancellationToken ct = default)
    {
        var questions = await _db.KnowledgeQuestions.AsNoTracking().Where(q => q.TenantId == tenantId).OrderBy(q => q.Category).ThenBy(q => q.SortOrder).ThenBy(q => q.Text).ToListAsync(ct);
        var answers = await _db.KnowledgeAnswers.AsNoTracking().Where(a => a.TenantId == tenantId && a.IsCurrent).ToListAsync(ct);
        var ids = answers.Select(a => a.Id).ToList();
        var evidence = await _db.AnswerEvidences.AsNoTracking().Where(e => e.TenantId == tenantId && ids.Contains(e.AnswerId)).ToListAsync(ct);
        var conflicts = await _db.KnowledgeConflicts.AsNoTracking().Where(c => c.TenantId == tenantId && c.AnswerId != null && ids.Contains(c.AnswerId.Value)).ToListAsync(ct);

        var text = RenderDraft(questions, answers, evidence, conflicts, DateTime.UtcNow);
        return await WithCopyAsync(tenantId, "product-knowledge-draft.txt", "files/draft/product-knowledge-draft.txt", text, ct);
    }

    internal static string RenderDraft(
        IReadOnlyList<KnowledgeQuestion> questions, IReadOnlyList<KnowledgeAnswer> answers, IReadOnlyList<AnswerEvidence> evidence, IReadOnlyList<KnowledgeConflict> conflicts, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PRODUCT KNOWLEDGE - DRAFT - NOT APPROVED");
        sb.AppendLine(Rule);
        sb.AppendLine("This is the working draft. Nothing in it is approved or published, and customers are never told anything from it.");
        sb.AppendLine("The approved record is the Final file of a published version.");
        sb.AppendLine($"Generated: {now:d MMM yyyy HH:mm} UTC");
        sb.AppendLine();

        var number = 0;
        foreach (var group in questions.GroupBy(q => q.Category))
        {
            sb.AppendLine(group.Key.ToUpperInvariant());
            sb.AppendLine(Rule);
            foreach (var question in group)
            {
                number++;
                var answer = answers.FirstOrDefault(a => a.QuestionId == question.Id);
                sb.AppendLine($"{number}. {question.Text}");
                if (answer is null)
                {
                    sb.AppendLine("   Status: NOT DRAFTED YET");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"   Status: {StatusText(answer.Status).ToUpperInvariant()} - {(answer.Status == KnowledgeVerificationStatus.VerifiedAndApproved ? "approved by you, but this is still the draft file" : "NOT APPROVED")}");
                if (answer.IsSimulated)
                    sb.AppendLine("   TEST DATA: written by the platform's test mode. It read nothing and is not real.");
                sb.AppendLine($"   Confidence in the evidence: {answer.Confidence}");
                if (answer.NeedsRevalidation)
                    sb.AppendLine($"   NEEDS ANOTHER LOOK: {answer.RevalidationReason}");
                if (!string.IsNullOrWhiteSpace(answer.Summary))
                {
                    sb.AppendLine($"   Answer: {answer.Summary}");
                    if (!string.IsNullOrWhiteSpace(answer.Detail))
                        foreach (var line in answer.Detail!.Split('\n'))
                            sb.AppendLine($"     {line.TrimEnd()}");
                }
                else
                {
                    sb.AppendLine("   Answer: none - the sources do not answer this.");
                }

                if (answer.IsMaterialClaim)
                    sb.AppendLine("   This states a price, guarantee, certification or result: it needs your explicit approval.");
                if (answer.HasUnsupportedClaim)
                    sb.AppendLine("   WARNING: it contains a figure that none of your sources states.");
                if (!string.IsNullOrWhiteSpace(answer.Limitation))
                    sb.AppendLine($"   Not settled by the sources: {answer.Limitation}");
                if (!string.IsNullOrWhiteSpace(answer.ClarifyingQuestion))
                    sb.AppendLine($"   Still to ask you: {answer.ClarifyingQuestion}");

                foreach (var c in conflicts.Where(c => c.AnswerId == answer.Id))
                    sb.AppendLine(c.Status == KnowledgeConflictStatus.Open
                        ? $"   CONFLICT (unresolved): your sources disagree about the {c.Fact}."
                        : $"   Conflict about the {c.Fact} resolved: you chose \"{c.ChosenValue}\".");

                foreach (var e in evidence.Where(e => e.AnswerId == answer.Id))
                    sb.AppendLine($"   Source: \"{OneLine(e.Excerpt)}\" ({e.SourceName}, {e.Location})");
                sb.AppendLine();
            }
        }

        var open = questions.Count(q =>
        {
            var a = answers.FirstOrDefault(x => x.QuestionId == q.Id);
            return a is null || a.Status is KnowledgeVerificationStatus.PendingVerification or KnowledgeVerificationStatus.CorrectionRequired or KnowledgeVerificationStatus.Rejected;
        });
        sb.AppendLine(Rule);
        sb.AppendLine($"{questions.Count} questions, {open} still unresolved.");
        return sb.ToString();
    }

    // ----- Final -----------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeFileResult> BuildFinalAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var (version, text) = await RenderFinalAsync(tenantId, versionId, ct);
        return await WithCopyAsync(tenantId, $"product-knowledge-v{version.Number}.txt", $"files/final/product-knowledge-v{version.Number}.txt", text, ct);
    }

    public async Task SaveFinalCopyAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        try
        {
            await BuildFinalAsync(tenantId, versionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not keep the Final knowledge file of version {VersionId} for tenant {TenantId}.", versionId, tenantId);
        }
    }

    private async Task<(KnowledgeVersion Version, string Text)> RenderFinalAsync(Guid tenantId, Guid versionId, CancellationToken ct)
    {
        var version = await _db.KnowledgeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct)
            ?? throw new NotFoundException("KnowledgeVersion", versionId);
        var items = await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.VersionId == version.Id).OrderBy(i => i.Category).ThenBy(i => i.Question).ToListAsync(ct);
        return (version, RenderFinal(version, items));
    }

    internal static string RenderFinal(KnowledgeVersion version, IReadOnlyList<KnowledgeItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PRODUCT KNOWLEDGE - VERSION {version.Number} - APPROVED");
        sb.AppendLine(Rule);
        sb.AppendLine($"Published: {version.PublishedAt:d MMM yyyy HH:mm} UTC{(string.IsNullOrEmpty(version.PublishedByEmail) ? "" : " by " + version.PublishedByEmail)}");
        sb.AppendLine($"Status: {(version.Status == KnowledgeVersionStatus.Current ? "the current version" : "replaced by a newer version")}");
        if (version.RestoredFromNumber is { } from)
            sb.AppendLine($"Restored from version {from}.");
        if (!string.IsNullOrWhiteSpace(version.Note))
            sb.AppendLine($"Note: {version.Note}");
        sb.AppendLine("Only answers you approved are in this file. Each one shows the words of your documents or website it rests on.");
        sb.AppendLine();

        var number = 0;
        foreach (var group in items.GroupBy(i => i.Category))
        {
            sb.AppendLine(group.Key.ToUpperInvariant());
            sb.AppendLine(Rule);
            foreach (var item in group)
            {
                number++;
                sb.AppendLine($"{number}. {item.Question}");
                sb.AppendLine($"   {item.Summary}");
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    foreach (var line in item.Detail!.Split('\n'))
                        sb.AppendLine($"     {line.TrimEnd()}");
                sb.AppendLine($"   Approved {item.ApprovedAt:d MMM yyyy}. Confidence in the evidence: {item.Confidence}.");
                if (item.IsMaterialClaim && item.ReviewDueAt is { } due)
                    sb.AppendLine($"   A price, guarantee or result: to be confirmed again by {due:d MMM yyyy}.");
                foreach (var e in ReadEvidence(item.EvidenceJson))
                    sb.AppendLine($"   Source: \"{OneLine(e.Excerpt)}\" ({e.SourceName}, {e.Location})");
                sb.AppendLine();
            }
        }

        sb.AppendLine(Rule);
        sb.AppendLine($"{items.Count} approved answers.");
        return sb.ToString();
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private async Task<KnowledgeFileResult> WithCopyAsync(Guid tenantId, string fileName, string relativePath, string text, CancellationToken ct)
    {
        // UTF-8 with a byte-order mark, so Windows editors open Indian-language and rupee text correctly.
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        string? key = null;
        if (_store.IsConfigured)
        {
            try
            {
                await using var stream = new MemoryStream(bytes);
                key = await _store.SaveAsync(tenantId, relativePath, stream, "text/plain; charset=utf-8", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not keep a copy of {File} for tenant {TenantId}.", fileName, tenantId);
            }
        }

        return new KnowledgeFileResult(fileName, bytes, key);
    }

    private static IReadOnlyList<KnowledgePublishingService.PublishedEvidence> ReadEvidence(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<KnowledgePublishingService.PublishedEvidence>>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return Array.Empty<KnowledgePublishingService.PublishedEvidence>();
        }
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string StatusText(KnowledgeVerificationStatus status) => status switch
    {
        KnowledgeVerificationStatus.VerifiedAndApproved => "Approved",
        KnowledgeVerificationStatus.CorrectionRequired => "Needs correction",
        KnowledgeVerificationStatus.Rejected => "Rejected",
        KnowledgeVerificationStatus.InformationNotAvailable => "Information not available",
        KnowledgeVerificationStatus.NotApplicable => "Does not apply",
        _ => "Waiting for your check",
    };
}
