using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>One embedding vector for one KnowledgeBaseChunk, produced by one embedding provider -
/// "Simulated"/"OpenAI"/"Google". A chunk can have up to one row per provider (unique index on
/// ChunkId+Provider, enforced by KnowledgeBaseChunkEmbeddingConfiguration), which is what lets the
/// admin UI show "this content has been embedded for OpenAI AND Google AND Simulated" instead of only
/// whichever one is currently active - the same "one row per (article, provider)" shape as
/// KnowledgeBaseArticleModelPublication, one layer down at the chunk/vector level instead of the
/// article/chat-model level.
///
/// KnowledgeBaseChunk.Embedding/EmbeddingProvider/EmbeddingModel still separately hold a copy of
/// whichever row here matches the currently-active AiProviders:EmbeddingProvider - that is what
/// RetrieveRelevantChunksAsync's cosine similarity actually reads, so retrieval logic did not need to
/// change when this table was added. This table is the full record; the chunk's own columns are a
/// convenience cache of "the one row retrieval currently cares about".</summary>
public class KnowledgeBaseChunkEmbedding : BaseEntity
{
    public Guid ChunkId { get; set; }

    /// <summary>"Simulated"/"OpenAI"/"Google" - IEmbeddingService.ProviderName at embed time.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>IEmbeddingService.ModelName at embed time, e.g. "text-embedding-3-small".</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>JSON array of floats - same encoding as KnowledgeBaseChunk.Embedding, see its own doc
    /// comment for why JSON text rather than varbinary/vector.</summary>
    public string Embedding { get; set; } = string.Empty;
}
