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
}
