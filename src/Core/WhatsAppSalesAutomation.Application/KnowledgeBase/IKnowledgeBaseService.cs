using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public interface IKnowledgeBaseService
{
    Task<PagedResult<KnowledgeBaseArticleDto>> GetPagedAsync(PagedRequest request, string? status = null, CancellationToken cancellationToken = default);

    Task<KnowledgeBaseArticleDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<KnowledgeBaseArticleDto> CreateAsync(CreateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken = default);

    Task<KnowledgeBaseArticleDto> UpdateAsync(Guid id, UpdateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Soft delete. A currently-Published article's already-embedded chunks are removed with
    /// it (Cascade) - an agent deleting an article is expected to mean "stop the AI citing this",
    /// not "keep it retrievable".</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Two behaviors merged behind one optional parameter, deliberately kept as one method rather
    /// than two - see the git history around 2026-09-07 for the "should these be separate endpoints"
    /// discussion this resolved.
    ///
    /// <paramref name="provider"/> == null (the common case): full canonical publish. Always
    /// re-chunks Content, embeds every available provider (see IEmbeddingProviderCatalog), replaces
    /// ALL of this article's previous chunks and their embeddings, sets Status = Published, and
    /// records <paramref name="approvedByUserId"/> - this is the "review and approve this content"
    /// action. Safe to call again on an already-Published article to pick up edited Content.
    ///
    /// <paramref name="provider"/> given (one of "Simulated"/"OpenAI"/"Google", case-insensitive):
    /// targeted, surgical publish. Only re-chunks if the article has no chunks yet or its existing
    /// ones are stale (EmbeddedFromArticleVersion behind the current Version - Content was edited
    /// since they were split); otherwise reuses the existing chunk text as-is. Only embeds via the
    /// named provider - every OTHER provider's existing embeddings for this article are left
    /// untouched, not wiped. Does NOT set ApprovedBy - this is "also cover this article for one more
    /// model", not a content review. Throws a FluentValidation.ValidationException if the provider
    /// name doesn't match a known IEmbeddingService, or if that provider has no API key configured.
    /// </summary>
    Task<KnowledgeBaseArticleDto> PublishAsync(Guid id, Guid approvedByUserId, string? provider = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes several articles in one call - each one still runs PublishAsync's full
    /// re-chunk/re-embed individually (this is not a bulk UPDATE), but a not-found or failed id is
    /// reported rather than aborting the rest of the batch.</summary>
    Task<BulkPublishArticlesResultDto> BulkPublishAsync(BulkPublishArticlesRequest request, Guid approvedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Makes this article eligible for retrieval when <paramref name="provider"/> (one of
    /// AiModelProvider's names, case-insensitive - "OpenAI"/"Google"/"Anthropic") is the active chat
    /// model - see KnowledgeBaseArticleModelPublication's doc comment. If the article is still Draft,
    /// this also runs PublishAsync's chunk/embed step first. Idempotent: publishing to a model the
    /// article is already published to just refreshes PublishedAt/PublishedBy. Throws a
    /// FluentValidation.ValidationException if <paramref name="provider"/> doesn't parse.</summary>
    Task<KnowledgeBaseArticleDto> PublishToModelAsync(Guid id, string provider, Guid publishedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Removes this article's eligibility for <paramref name="provider"/>'s retrieval. Does
    /// not touch Status or the article's chunks - those stay as-is for any other model the article is
    /// still published to. A no-op (not an error) if the article was not published to this model.
    /// </summary>
    Task<KnowledgeBaseArticleDto> UnpublishFromModelAsync(Guid id, string provider, CancellationToken cancellationToken = default);

    /// <summary>Re-chunks and re-embeds every Published article whose chunks are stale (embedded from
    /// an older Version than the article's current one) - the bulk/scheduled counterpart to calling
    /// PublishAsync on one article by hand.</summary>
    Task ReindexAsync(CancellationToken cancellationToken = default);

    /// <summary>In-application cosine similarity over every chunk belonging to a Published article -
    /// see KnowledgeBaseChunk's doc comment for why this is not a database-side vector search. Returns
    /// at most <c>AiOptions.KnowledgeBaseTopN</c> chunks, only those at or above
    /// <c>AiOptions.MinRelevanceScore</c>.</summary>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveRelevantChunksAsync(string query, CancellationToken cancellationToken = default);
}
