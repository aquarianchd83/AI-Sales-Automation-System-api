using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>One retrieval-sized slice of a published KnowledgeBaseArticle, plus its embedding.
///
/// Embedding is stored as a JSON-encoded float array in a plain nvarchar(max) column rather than
/// varbinary or a native vector type - this phase's chosen RAG approach is in-application cosine
/// similarity (fetch candidate chunks, compute similarity in the Application layer), not a SQL Server
/// native vector index, so there is no requirement to store in a query-optimized binary/vector format.
/// JSON keeps the value human-inspectable for debugging and trivially portable if the storage format
/// changes later (e.g. to SQL Server's native VECTOR type or an external vector DB) without a data
/// migration beyond "re-embed everything", which KnowledgeBaseReindexJob already exists to do.</summary>
public class KnowledgeBaseChunk : BaseEntity
{
    public Guid ArticleId { get; set; }

    public int ChunkIndex { get; set; }

    public string ChunkText { get; set; } = string.Empty;

    /// <summary>JSON array of floats, e.g. "[0.012,-0.34,...]". Null until the embedding provider has
    /// run - a chunk can exist (freshly split from the article) slightly before it is embedded.</summary>
    public string? Embedding { get; set; }

    public int TokenCount { get; set; }

    /// <summary>Snapshot of KnowledgeBaseArticle.Version at embedding time - lets a reindex job detect
    /// chunks embedded from a since-edited article version without re-reading the article every time.</summary>
    public int EmbeddedFromArticleVersion { get; set; }

    /// <summary>Which IEmbeddingService produced the current Embedding value - "Simulated"/"OpenAI"/
    /// "Google", from IEmbeddingService.ProviderName at the moment ReembedAsync ran. Null until
    /// embedded, same as Embedding itself. Exists so a chunk that was embedded under one provider
    /// (e.g. "Simulated", before a real provider was configured) doesn't silently look identical, in
    /// the admin UI, to one embedded for real - see IEmbeddingService.ProviderName's doc comment.</summary>
    public string? EmbeddingProvider { get; set; }

    /// <summary>The specific model within EmbeddingProvider - IEmbeddingService.ModelName at embed
    /// time, e.g. "text-embedding-3-small". Null until embedded, same as EmbeddingProvider.</summary>
    public string? EmbeddingModel { get; set; }
}
