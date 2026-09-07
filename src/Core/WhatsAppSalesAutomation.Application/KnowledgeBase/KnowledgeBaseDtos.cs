namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

public record KnowledgeBaseArticleDto(
    Guid Id,
    string Title,
    string? Category,
    string SourceType,
    string Content,
    string Status,
    int Version,
    Guid? ApprovedBy,
    int ChunkCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<ArticleModelPublicationDto> PublishedModels,
    /// <summary>Which IEmbeddingService is the currently-active one's copy of this article's
    /// embedding - "Simulated"/"OpenAI"/"Google" - null for a Draft article that has never been
    /// embedded. This is what RetrieveRelevantChunksAsync's cosine similarity actually uses. See
    /// EmbeddedProviders for the full multi-provider picture; this is just "the one retrieval cares
    /// about right now".</summary>
    string? EmbeddingProvider,
    string? EmbeddingModel,
    /// <summary>Every provider this article's chunks have actually been embedded for - "published to
    /// a chat model" (PublishedModels) and "embedded by a provider" (this) are independent axes: an
    /// article can be published to the OpenAI chat model while its chunks were last embedded only by
    /// Simulated (e.g. re-embedded while AiProviders:EmbeddingProvider was Simulated) - see
    /// KnowledgeBaseChunkEmbedding's own doc comment for why both are tracked separately.</summary>
    IReadOnlyList<ArticleEmbeddingProviderDto> EmbeddedProviders);

/// <summary>One provider a KnowledgeBaseArticle's chunks have been embedded for - see
/// KnowledgeBaseChunkEmbedding's doc comment. EmbeddedAt is the first chunk's CreatedAt for that
/// provider (all chunks of one article are (re)embedded together in one ReembedAsync call, so they
/// share the same timestamp in practice).</summary>
public record ArticleEmbeddingProviderDto(string Provider, string Model, DateTime EmbeddedAt);

/// <summary>One AI chat model this article has been explicitly published to - see
/// KnowledgeBaseArticleModelPublication's doc comment. An article with an empty PublishedModels
/// list is chunked/embedded (if Status is Published) but not yet eligible for any model's
/// retrieval.</summary>
public record ArticleModelPublicationDto(string Provider, DateTime PublishedAt, Guid? PublishedBy);

public record CreateKnowledgeBaseArticleRequest(string Title, string? Category, string Content, string SourceType);

/// <summary>Editing Content bumps KnowledgeBaseArticle.Version but does not re-chunk/embed by itself -
/// an already-Published article keeps serving its old chunks (embedded from the old Version) until
/// PublishAsync or ReindexAsync is called again, so an edit can be saved as a draft-in-progress without
/// affecting what the AI is currently grounded on.</summary>
public record UpdateKnowledgeBaseArticleRequest(string Title, string? Category, string Content);

/// <summary>What RetrieveRelevantChunksAsync returns - the chunk's text plus how well it matched the
/// query, for both grounding the AI's prompt and recording AiInteractionSource rows.</summary>
public record RetrievedChunk(Guid ChunkId, Guid ArticleId, string Text, double RelevanceScore);

public record BulkPublishArticlesRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Result of a bulk publish. Unlike a bulk delete, this cannot be a single UPDATE batch - each
/// article is genuinely re-chunked and re-embedded (PublishAsync's real work), so one bad id (not
/// found) or one provider failure (an embedding call throwing) must not abort the ones that would
/// otherwise have succeeded. Both are reported rather than failing the whole call, same reasoning as
/// BulkDeleteCustomersResultDto.
/// </summary>
public record BulkPublishArticlesResultDto(
    int RequestedCount,
    int PublishedCount,
    IReadOnlyList<Guid> NotFoundIds,
    IReadOnlyList<Guid> FailedIds);

/// <summary>What the article-list UI should actually offer publish/embed badges for - deployments
/// without a real API key configured for a given chat model or embedding provider can't do
/// anything useful with it, so it's left out entirely rather than shown disabled. "Simulated" needs
/// no key and is always included in EmbeddingProviders; ChatModels only ever lists
/// "OpenAI"/"Google"/"Anthropic" (AiModelProvider has no Simulated entry - see its own doc
/// comment), and only the ones with a configured key.</summary>
public record AvailableAiProvidersDto(IReadOnlyList<string> ChatModels, IReadOnlyList<string> EmbeddingProviders);
