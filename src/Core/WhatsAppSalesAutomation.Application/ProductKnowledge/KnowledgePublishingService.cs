using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <param name="Code">NeedsRecheck (a source changed), ReviewDue (an approved price or guarantee has lapsed), OpenConflict, TestData.</param>
public record HeldBackDto(Guid QuestionId, string Question, string Code, string Reason);

/// <param name="ServingStatus">Indexing, Live (chat reads this version's chunks), Failed (publishing stands; chat keeps the last Live version) or Replaced.</param>
/// <param name="ServingError">Why indexing failed, in a sentence.</param>
/// <param name="ServingIndexedAt">UTC.</param>
public record KnowledgeVersionDto(
    Guid Id, int Number, string Status, DateTime PublishedAt, string? PublishedByEmail, int ItemCount, string? Note, int? RestoredFromNumber,
    string ServingStatus = "Indexing", string? ServingError = null, DateTime? ServingIndexedAt = null);

/// <param name="Ready">Approved answers that would be published now.</param>
/// <param name="HeldBack">Approved answers that must be looked at again first: they stay out of the next version, and whatever is published already stays as it is.</param>
/// <param name="NotYetApproved">Questions with no approved answer: drafted and waiting, rejected, unknown, not applicable or not drafted.</param>
/// <param name="NothingNew">True when publishing now would repeat the current version exactly.</param>
public record PublishPreviewDto(int Ready, IReadOnlyList<HeldBackDto> HeldBack, int NotYetApproved, bool NothingNew, KnowledgeVersionDto? Current);

/// <param name="ChatUsesPublishedKnowledge">The platform administrator's per-tenant switch: ON, chat answers only from the published version; OFF, from the knowledge base articles as before.</param>
/// <param name="ServingVersionNumber">The version whose chunks chat can read now (the latest that went Live), or null if none has.</param>
public record KnowledgeVersionsOverviewDto(
    PublishPreviewDto Preview, IReadOnlyList<KnowledgeVersionDto> Versions, bool ChatUsesPublishedKnowledge = false, int? ServingVersionNumber = null);

public record KnowledgeItemDto(
    Guid Id, string Category, string Question, string Summary, string? Detail, string Kind, string Confidence, bool IsMaterialClaim, DateTime ApprovedAt, DateTime? ReviewDueAt,
    IReadOnlyList<AnswerEvidenceDto> Evidence);

public record KnowledgeVersionDetailDto(KnowledgeVersionDto Version, IReadOnlyList<KnowledgeItemDto> Items);

public record PublishRequest(string? Note);

public interface IKnowledgePublishingService
{
    Task<KnowledgeVersionsOverviewDto> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<KnowledgeVersionDetailDto> GetVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Publishes the approved answers as the next version. Answers needing another look are held back, never published.</summary>
    Task<KnowledgeVersionDto> PublishAsync(Guid tenantId, PublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>Publishes a new version that copies an earlier one. Nothing is erased or rewritten: the versions in between stay on record.</summary>
    Task<KnowledgeVersionDto> RestoreAsync(Guid tenantId, Guid versionId, PublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>Builds the chat index of the current version again (after a failure). The version itself is not changed.</summary>
    Task<KnowledgeVersionDto> ReindexAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishing is the one step that decides what customers can be told. Only what the tenant approved is eligible - not drafts, not test data,
/// not an answer whose source has changed since, not a price or guarantee whose approval has lapsed, not an answer whose sources still
/// disagree. What is published is an immutable snapshot (a version) with the quotations and who approved each answer and when; a newer
/// version supersedes it but never erases it, and restoring an old one is a new version, so the history of what was published is whole.
/// </summary>
public sealed class KnowledgePublishingService : IKnowledgePublishingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _user;
    private readonly IKnowledgeServingIndexer? _indexer;
    private readonly IProductKnowledgeServing? _serving;

    public KnowledgePublishingService(IApplicationDbContext db, ICurrentUserService user, IKnowledgeServingIndexer? indexer = null, IProductKnowledgeServing? serving = null)
    {
        _db = db;
        _user = user;
        _indexer = indexer;
        _serving = serving;
    }

    private sealed record Candidate(KnowledgeQuestion Question, KnowledgeAnswer Answer);

    private sealed record Evaluation(List<Candidate> Ready, List<HeldBackDto> HeldBack, int NotYetApproved);

    // ----- reading ---------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeVersionsOverviewDto> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var versions = await _db.KnowledgeVersions.AsNoTracking().Where(v => v.TenantId == tenantId).OrderByDescending(v => v.Number).ToListAsync(ct);
        var current = versions.FirstOrDefault(v => v.Status == KnowledgeVersionStatus.Current);
        var evaluation = await EvaluateAsync(tenantId, ct);

        var nothingNew = false;
        if (current is not null)
        {
            var published = (await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.VersionId == current.Id).Select(i => i.AnswerId).ToListAsync(ct)).ToHashSet();
            nothingNew = published.SetEquals(evaluation.Ready.Select(c => c.Answer.Id));
        }

        var preview = new PublishPreviewDto(evaluation.Ready.Count, evaluation.HeldBack, evaluation.NotYetApproved, nothingNew, current is null ? null : ToDto(current));
        var serving = versions.FirstOrDefault(v => v.ServingStatus == KnowledgeServingStatus.Live);
        var chatOn = _serving is not null && await _serving.IsEnabledForTenantAsync(tenantId, ct);
        return new KnowledgeVersionsOverviewDto(preview, versions.Select(ToDto).ToList(), chatOn, serving?.Number);
    }

    public async Task<KnowledgeVersionDetailDto> GetVersionAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await FindAsync(tenantId, versionId, ct);
        var items = await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.VersionId == version.Id).OrderBy(i => i.Category).ThenBy(i => i.Question).ToListAsync(ct);
        return new KnowledgeVersionDetailDto(ToDto(version), items.Select(ToDto).ToList());
    }

    // ----- publishing ------------------------------------------------------------------------------------------------------------

    public async Task<KnowledgeVersionDto> PublishAsync(Guid tenantId, PublishRequest request, CancellationToken ct = default)
    {
        var evaluation = await EvaluateAsync(tenantId, ct);
        if (evaluation.Ready.Count == 0)
        {
            throw new ConflictException(evaluation.HeldBack.Count > 0
                ? "Nothing can be published yet: the approved answers need another look first (see what was held back)."
                : "Nothing is approved yet. Approve at least one answer, then publish.");
        }

        var current = await _db.KnowledgeVersions.FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Status == KnowledgeVersionStatus.Current, ct);
        if (current is not null)
        {
            var published = (await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.VersionId == current.Id).Select(i => i.AnswerId).ToListAsync(ct)).ToHashSet();
            if (published.SetEquals(evaluation.Ready.Select(c => c.Answer.Id)))
                throw new ConflictException($"Nothing has changed since version {current.Number}, so there is nothing new to publish.");
        }

        var ids = evaluation.Ready.Select(c => c.Answer.Id).ToList();
        var evidence = await _db.AnswerEvidences.AsNoTracking().Where(e => e.TenantId == tenantId && ids.Contains(e.AnswerId)).ToListAsync(ct);
        var items = evaluation.Ready.Select(c => NewItem(tenantId, c.Question, c.Answer, evidence.Where(e => e.AnswerId == c.Answer.Id))).ToList();

        return await SaveVersionAsync(tenantId, current, items, Trim(request?.Note, 1000), null, ct);
    }

    public async Task<KnowledgeVersionDto> RestoreAsync(Guid tenantId, Guid versionId, PublishRequest request, CancellationToken ct = default)
    {
        var old = await FindAsync(tenantId, versionId, ct);
        if (old.Status == KnowledgeVersionStatus.Current)
            throw new ConflictException("This is already the current version.");

        var current = await _db.KnowledgeVersions.FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Status == KnowledgeVersionStatus.Current, ct);
        var copies = (await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.VersionId == old.Id).ToListAsync(ct))
            .Select(i => new KnowledgeItem
            {
                TenantId = tenantId, QuestionId = i.QuestionId, AnswerId = i.AnswerId, Category = i.Category, Question = i.Question, Summary = i.Summary, Detail = i.Detail, Kind = i.Kind,
                Confidence = i.Confidence, IsMaterialClaim = i.IsMaterialClaim, ApprovedBy = i.ApprovedBy, ApprovedAt = i.ApprovedAt, ReviewDueAt = i.ReviewDueAt, EvidenceJson = i.EvidenceJson
            }).ToList();

        return await SaveVersionAsync(tenantId, current, copies, Trim(request?.Note, 1000) ?? $"Restored from version {old.Number}.", old.Number, ct);
    }

    private async Task<KnowledgeVersionDto> SaveVersionAsync(Guid tenantId, KnowledgeVersion? current, List<KnowledgeItem> items, string? note, int? restoredFrom, CancellationToken ct)
    {
        var number = (await _db.KnowledgeVersions.Where(v => v.TenantId == tenantId).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        var version = new KnowledgeVersion
        {
            TenantId = tenantId, Number = number, Status = KnowledgeVersionStatus.Current, PublishedBy = _user.UserId, PublishedByEmail = Trim(_user.Email, 256),
            PublishedAt = DateTime.UtcNow, Note = note, ItemCount = items.Count, RestoredFromNumber = restoredFrom
        };

        if (current is not null)
            current.Status = KnowledgeVersionStatus.Superseded; // kept, never deleted
        _db.KnowledgeVersions.Add(version);
        foreach (var item in items)
        {
            item.VersionId = version.Id;
            _db.KnowledgeItems.Add(item);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("Someone published at the same moment. Reload and check the current version before publishing again.");
        }

        // The version is published. Building the chat index is a separate step: if it does not finish, the publish stands and the version says so.
        await IndexForChatAsync(tenantId, version, ct);
        return ToDto(version);
    }

    public async Task<KnowledgeVersionDto> ReindexAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await FindAsync(tenantId, versionId, ct);
        if (version.Status != KnowledgeVersionStatus.Current)
            throw new ConflictException("Only the current published version can be indexed for chat.");
        if (version.ServingStatus == KnowledgeServingStatus.Live)
            throw new ConflictException("This version is already live for chat.");
        if (version.ServingStatus == KnowledgeServingStatus.Indexing && (version.UpdatedAt ?? version.CreatedAt) > DateTime.UtcNow.AddMinutes(-StaleIndexingMinutes))
            throw new ConflictException("This version is being indexed right now. Check again in a moment.");

        await IndexForChatAsync(tenantId, version, ct);
        return ToDto(version);
    }

    /// <summary>A version stuck in Indexing for longer than this (the process died mid-way) may be indexed again.</summary>
    private const int StaleIndexingMinutes = 15;

    private async Task IndexForChatAsync(Guid tenantId, KnowledgeVersion version, CancellationToken ct)
    {
        if (_indexer is null)
            return;

        try
        {
            await _indexer.IndexVersionAsync(tenantId, version.Id, _user.UserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The indexer records its own failures; this is only for one it could not (so a published version is never left saying "Indexing" forever).
            version.ServingStatus = KnowledgeServingStatus.Failed;
            version.ServingError = "Indexing for chat did not finish. Try again in a moment.";
            version.ServingIndexedAt = DateTime.UtcNow;
            try { await _db.SaveChangesAsync(CancellationToken.None); } catch (Exception) { /* nothing more can be done here */ }
        }
    }

    // ----- what is eligible ----------------------------------------------------------------------------------------------------------

    private async Task<Evaluation> EvaluateAsync(Guid tenantId, CancellationToken ct)
    {
        var questions = await _db.KnowledgeQuestions.AsNoTracking().Where(q => q.TenantId == tenantId).OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt).ToListAsync(ct);
        var answers = await _db.KnowledgeAnswers.AsNoTracking().Where(a => a.TenantId == tenantId && a.IsCurrent).ToListAsync(ct);
        var answerIds = answers.Select(a => a.Id).ToList();
        var openConflicts = (await _db.KnowledgeConflicts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == KnowledgeConflictStatus.Open && c.AnswerId != null && answerIds.Contains(c.AnswerId.Value)).Select(c => c.AnswerId!.Value).ToListAsync(ct)).ToHashSet();

        var now = DateTime.UtcNow;
        var ready = new List<Candidate>();
        var held = new List<HeldBackDto>();
        var notApproved = 0;

        foreach (var question in questions)
        {
            var answer = answers.FirstOrDefault(a => a.QuestionId == question.Id);
            if (answer is null || answer.Status != KnowledgeVerificationStatus.VerifiedAndApproved || string.IsNullOrWhiteSpace(answer.Summary))
            {
                notApproved++;
                continue;
            }

            if (answer.IsSimulated)
                held.Add(new HeldBackDto(question.Id, question.Text, "TestData", "This is test data from the simulator, not an answer."));
            else if (answer.NeedsRevalidation)
                held.Add(new HeldBackDto(question.Id, question.Text, "NeedsRecheck", answer.RevalidationReason ?? "A source it was drawn from changed."));
            else if (answer.ReviewDueAt is { } due && due < now)
                held.Add(new HeldBackDto(question.Id, question.Text, "ReviewDue", $"Your approval of this price, guarantee or result lapsed on {due:d MMM yyyy}. Confirm it is still current."));
            else if (openConflicts.Contains(answer.Id))
                held.Add(new HeldBackDto(question.Id, question.Text, "OpenConflict", "Your sources disagree about a fact in this answer. Choose the right value."));
            else
                ready.Add(new Candidate(question, answer));
        }

        return new Evaluation(ready, held, notApproved);
    }

    private static KnowledgeItem NewItem(Guid tenantId, KnowledgeQuestion question, KnowledgeAnswer answer, IEnumerable<AnswerEvidence> evidence) => new()
    {
        TenantId = tenantId, QuestionId = question.Id, AnswerId = answer.Id, Category = question.Category, Question = question.Text, Summary = answer.Summary!, Detail = answer.Detail,
        Kind = answer.Kind, Confidence = answer.Confidence, IsMaterialClaim = answer.IsMaterialClaim, ApprovedBy = answer.ApprovedBy, ApprovedAt = answer.ApprovedAt ?? DateTime.UtcNow,
        ReviewDueAt = answer.ReviewDueAt,
        EvidenceJson = JsonSerializer.Serialize(evidence.Select(e => new PublishedEvidence(e.SourceId, e.SourceName, e.Location, e.Heading, e.Excerpt, e.SourceVersion, e.PassageHash)).ToList(), Json)
    };

    /// <summary>A quotation as it was published: where it came from, and the version and fingerprint of the passage - so the link to its source survives.</summary>
    internal sealed record PublishedEvidence(Guid SourceId, string SourceName, string Location, string? Heading, string Excerpt, int SourceVersion, string PassageHash);

    // ----- helpers -----------------------------------------------------------------------------------------------------------------

    private async Task<KnowledgeVersion> FindAsync(Guid tenantId, Guid versionId, CancellationToken ct) =>
        await _db.KnowledgeVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("KnowledgeVersion", versionId);

    private static KnowledgeVersionDto ToDto(KnowledgeVersion v) =>
        new(v.Id, v.Number, v.Status.ToString(), v.PublishedAt, v.PublishedByEmail, v.ItemCount, v.Note, v.RestoredFromNumber, v.ServingStatus.ToString(), v.ServingError, v.ServingIndexedAt);

    private static KnowledgeItemDto ToDto(KnowledgeItem i)
    {
        List<PublishedEvidence> evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<List<PublishedEvidence>>(i.EvidenceJson, Json) ?? new List<PublishedEvidence>();
        }
        catch (JsonException)
        {
            evidence = new List<PublishedEvidence>();
        }

        return new KnowledgeItemDto(
            i.Id, i.Category, i.Question, i.Summary, i.Detail, i.Kind.ToString(), i.Confidence.ToString(), i.IsMaterialClaim, i.ApprovedAt, i.ReviewDueAt,
            evidence.Select(e => new AnswerEvidenceDto(e.SourceId, e.SourceName, e.Location, e.Heading, e.Excerpt)).ToList());
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        return v.Length <= max ? v : v[..max];
    }
}
