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

    /// <summary>Chunks Content, embeds each chunk via IEmbeddingService, replaces any previous chunks
    /// for this article, and sets Status = Published. Safe to call again on an already-Published
    /// article to pick up an edited Content (re-chunks/re-embeds from the current Version).</summary>
    Task<KnowledgeBaseArticleDto> PublishAsync(Guid id, Guid approvedByUserId, CancellationToken cancellationToken = default);

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
