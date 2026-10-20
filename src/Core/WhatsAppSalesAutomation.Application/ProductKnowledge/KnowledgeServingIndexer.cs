using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>
/// The SERVING index: what customer chat reads. A published knowledge version is rendered as the markdown of the tenant's one system-managed article
/// (a section per item) and run through the existing publish pipeline, so chunking, the safety scan, embedding and the atomic swap are the platform's own.
/// Only the items of the published version go in - never a draft, never an item that was held back - and nothing from the source index is ever here.
/// </summary>
public interface IKnowledgeServingIndexer
{
    /// <summary>
    /// Builds the serving index for the tenant's CURRENT published version and records the outcome on the version. Never throws for an indexing problem: a failure
    /// is recorded as Failed with a reason, publishing is not undone, and chat keeps reading the last version that went Live. Safe to call again to retry.
    /// </summary>
    Task<KnowledgeServingStatus> IndexVersionAsync(Guid tenantId, Guid versionId, Guid? actingUserId, CancellationToken cancellationToken = default);
}

public sealed class KnowledgeServingIndexer : IKnowledgeServingIndexer
{
    private const string FallbackError = "Indexing for chat did not finish. Try again in a moment.";

    private readonly IApplicationDbContext _db;
    private readonly IKnowledgeBaseService _articles;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<KnowledgeServingIndexer> _logger;

    public KnowledgeServingIndexer(IApplicationDbContext db, IKnowledgeBaseService articles, IDateTimeProvider clock, ILogger<KnowledgeServingIndexer> logger)
    {
        _db = db;
        _articles = articles;
        _clock = clock;
        _logger = logger;
    }

    public async Task<KnowledgeServingStatus> IndexVersionAsync(Guid tenantId, Guid versionId, Guid? actingUserId, CancellationToken ct = default)
    {
        var version = await _db.KnowledgeVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct)
            ?? throw new NotFoundException("KnowledgeVersion", versionId);

        // Only the current version is indexed: indexing an older one would put superseded answers in front of customers.
        if (version.Status != KnowledgeVersionStatus.Current)
            throw new ConflictException("Only the current published version is indexed for chat.");

        version.ServingStatus = KnowledgeServingStatus.Indexing;
        version.ServingError = null;
        await _db.SaveChangesAsync(ct);

        try
        {
            await BuildAsync(tenantId, version, actingUserId ?? version.PublishedBy, ct);
            await MarkLiveAsync(tenantId, version, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkFailedAsync(version, Describe(ex), ex, ct);
        }

        return version.ServingStatus;
    }

    private async Task BuildAsync(Guid tenantId, KnowledgeVersion version, Guid? approver, CancellationToken ct)
    {
        if (approver is not { } approvedBy)
            throw new InvalidOperationException("No user to record as the approver of the serving article.");

        // Straight from the published version's own items - the immutable snapshot - not from the working answers.
        var items = await _db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.VersionId == version.Id).ToListAsync(ct);
        if (items.Count == 0)
            throw new InvalidOperationException("This version has no published answers to index.");

        var content = ServingArticleRenderer.Render(items);
        var hash = ServingArticleRenderer.HashOf(content);

        var article = await _db.KnowledgeBaseArticles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.SystemKey == ProductKnowledgeSystem.ArticleKey && !a.IsDeleted, ct);

        if (article is null)
        {
            article = new KnowledgeBaseArticle
            {
                TenantId = tenantId,
                SystemKey = ProductKnowledgeSystem.ArticleKey,
                ArticleKey = "product-knowledge",
                Title = ProductKnowledgeSystem.ArticleTitle,
                SubCategory = "Product knowledge",
                Category = KnowledgeCategory.GettingStarted,
                SourceType = KnowledgeSourceType.AdminConfiguredArticle,
                AuthorityRank = KnowledgeAuthority.RankFor(KnowledgeSourceType.AdminConfiguredArticle, isGlobal: false),
                Status = KnowledgeArticleStatus.Draft,
                VersionNumber = 1,
                IsCurrentVersion = true,
                EffectiveFrom = _clock.UtcNow,
                Content = content,
                ContentHash = hash
            };
            _db.KnowledgeBaseArticles.Add(article);
        }
        else if (article.ContentHash != hash)
        {
            // New text is a new article version, so the ingestion job is for exactly this text. The same text (a retry) keeps its version and resumes.
            article.Content = content;
            article.ContentHash = hash;
            article.VersionNumber++;
            article.LastUpdatedAt = _clock.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await _articles.PublishSystemArticleAsync(article.Id, approvedBy, ct);

        // Every active chunk of the article now belongs to this version.
        var chunks = await _db.KnowledgeBaseChunks.IgnoreQueryFilters().Where(c => c.ArticleId == article.Id && c.IsActive).ToListAsync(ct);
        foreach (var chunk in chunks)
            chunk.KnowledgeVersionId = version.Id;
        await _db.SaveChangesAsync(ct);

        // Integrity: every published item must have reached the index, or chat would silently not know an answer the tenant published.
        var indexed = chunks.Where(c => c.KnowledgeItemId != null).Select(c => c.KnowledgeItemId!.Value).ToHashSet();
        var missing = items.Count(i => !indexed.Contains(i.Id));
        if (missing > 0)
            throw new InvalidOperationException($"{missing} of {items.Count} published answers did not reach the chat index.");
    }

    private async Task MarkLiveAsync(Guid tenantId, KnowledgeVersion version, CancellationToken ct)
    {
        var others = await _db.KnowledgeVersions.Where(v => v.TenantId == tenantId && v.Id != version.Id && v.ServingStatus == KnowledgeServingStatus.Live).ToListAsync(ct);
        foreach (var other in others)
            other.ServingStatus = KnowledgeServingStatus.Replaced;

        version.ServingStatus = KnowledgeServingStatus.Live;
        version.ServingError = null;
        version.ServingIndexedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkFailedAsync(KnowledgeVersion version, string reason, Exception ex, CancellationToken ct)
    {
        _logger.LogWarning(ex, "Serving index for tenant {TenantId} version {Number} did not finish", version.TenantId, version.Number);

        // Whatever the failed attempt left half-tracked must not be saved with the status.
        version.ServingStatus = KnowledgeServingStatus.Failed;
        version.ServingError = reason.Length > 1000 ? reason[..1000] : reason;
        version.ServingIndexedAt = _clock.UtcNow;
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception saveEx) when (saveEx is not OperationCanceledException)
        {
            _logger.LogError(saveEx, "Could not record the serving index failure for version {VersionId}", version.Id);
        }
    }

    /// <summary>A sentence the tenant can read. A validation failure from the publish pipeline already is one; anything else gets a general sentence (the
    /// details are in the log).</summary>
    private static string Describe(Exception ex) => ex switch
    {
        FluentValidation.ValidationException v when v.Errors.FirstOrDefault()?.ErrorMessage is { Length: > 0 } message => message,
        InvalidOperationException io when io.Message.Length is > 0 and < 300 => io.Message,
        _ => FallbackError
    };
}
