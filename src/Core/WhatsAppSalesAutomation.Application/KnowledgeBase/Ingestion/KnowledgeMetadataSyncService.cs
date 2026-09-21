using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>
/// Pushes an article's metadata onto its existing chunks without re-embedding (§G.8).
///
/// Changing an article's AuthorityRank, adding a CountryCode, or moving its effective window are the
/// most common admin edits and change nothing about what the text MEANS. Re-embedding forty chunks for
/// one of them would pay a provider for vectors identical to the ones already stored, so this touches
/// only the denormalized filter columns.
///
/// What it deliberately does not touch: ContextHeader, EmbeddingInput and the vector. EmbeddingInput
/// is persisted precisely so it always equals what was embedded; rewriting the header inside it would
/// claim the stored vector covers text it never saw. The header text is therefore a snapshot until the
/// next real re-index, while every column retrieval FILTERS on is current - which is the part that
/// affects correctness.
/// </summary>
public sealed class KnowledgeMetadataSyncService
{
    private readonly IApplicationDbContext _context;

    public KnowledgeMetadataSyncService(IApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>True when the article's content has changed since its chunks were embedded, i.e. a
    /// metadata sync would leave stale text in the index and a re-index is needed instead.</summary>
    public async Task<bool> NeedsReindexAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var article = await FindAsync(articleId, cancellationToken);

        var hasCurrent = await _context.KnowledgeBaseChunks
            .AnyAsync(c => c.ArticleId == articleId && c.IsActive && c.EmbeddedFromArticleVersion == article.VersionNumber, cancellationToken);

        return !hasCurrent;
    }

    /// <summary>Returns how many chunks were updated. Zero for an article with no chunks yet.</summary>
    public async Task<int> SyncAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var article = await FindAsync(articleId, cancellationToken);

        var chunks = await _context.KnowledgeBaseChunks
            .Where(c => c.ArticleId == articleId)
            .ToListAsync(cancellationToken);

        foreach (var chunk in chunks)
            Apply(article, chunk);

        await _context.SaveChangesAsync(cancellationToken);
        return chunks.Count;
    }

    /// <summary>The filter columns, and only those. Kept as one method so the list of "what a sync
    /// touches" is readable in one place.</summary>
    internal static void Apply(KnowledgeBaseArticle article, KnowledgeBaseChunk chunk)
    {
        chunk.AuthorityRank = article.AuthorityRank;
        chunk.ProductModule = article.ProductModule;
        chunk.SourceType = article.SourceType;
        chunk.LanguageCode = article.LanguageCode;
        chunk.CountryCode = article.CountryCode;
        chunk.VersionMinNumeric = SemanticVersion.ToNumeric(article.AppliesToVersionMin);
        chunk.VersionMaxNumeric = SemanticVersion.ToNumeric(article.AppliesToVersionMax);
        chunk.ArticleStatus = article.Status;
        chunk.EffectiveFrom = article.EffectiveFrom;
        chunk.EffectiveTo = article.EffectiveTo;
        chunk.IsCurrentArticleVersion = article.IsCurrentVersion;
    }

    private async Task<KnowledgeBaseArticle> FindAsync(Guid articleId, CancellationToken cancellationToken) =>
        await _context.KnowledgeBaseArticles.FirstOrDefaultAsync(a => a.Id == articleId, cancellationToken)
        ?? throw new NotFoundException(nameof(KnowledgeBaseArticle), articleId);
}
