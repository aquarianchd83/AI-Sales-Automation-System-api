using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>
/// One retrieval-sized slice of a <see cref="KnowledgeBaseArticle"/>, plus its embedding and a copy
/// of every article field the retrieval filter needs.
///
/// <see cref="ITenantScopedOrGlobal"/> rather than <see cref="ITenantOwned"/>, following the article:
/// a GLOBAL article's chunks must be readable by every tenant, and a non-nullable TenantId cannot say
/// that. The two must agree, which a nightly integrity check verifies - a chunk whose tenant differs
/// from its article's is a cross-tenant leak waiting for a query that forgets to join.
///
/// The denormalized block at the bottom is written ONLY by the ingestion pipeline, never by an
/// article edit. That is deliberate: it is a snapshot of what was true when the chunk was embedded,
/// so a chunk cannot silently start claiming an authority or an effective window its own vector was
/// never built under. A metadata-only article edit refreshes these through a dedicated sync job
/// rather than through a full re-embed.
/// </summary>
public class KnowledgeBaseChunk : BaseEntity, ITenantScopedOrGlobal
{
    /// <summary>NULL = GLOBAL, matching the parent article. Kept equal to the article's by ingestion.</summary>
    public Guid? TenantId { get; set; }

    public Guid ArticleId { get; set; }

    public int ChunkIndex { get; set; }

    /// <summary>The breadcrumb plus applicability block prepended to every chunk so it reads on its
    /// own - "Billing > Refunds > India / applies to: country IN". Stored separately from
    /// <see cref="ChunkText"/> so the prompt can render it as structure rather than prose, and so a
    /// metadata-only edit can refresh it without touching the body.</summary>
    public string ContextHeader { get; set; } = string.Empty;

    /// <summary>The chunk body - what the AI actually reads as content.</summary>
    public string ChunkText { get; set; } = string.Empty;

    /// <summary><see cref="ContextHeader"/> + "\n\n" + <see cref="ChunkText"/>, i.e. exactly what was
    /// embedded. Persisted rather than recomputed so a later change to the header format cannot
    /// silently desynchronize the stored vector from the text it supposedly represents.</summary>
    public string EmbeddingInput { get; set; } = string.Empty;

    /// <summary>Non-null when this chunk is one piece of an atomic unit (a rule, a procedure, a table)
    /// that did not fit in one chunk. Retrieval that selects ANY chunk of a group pulls in the WHOLE
    /// group - see the atomic group expansion in retrieval. That rule is what makes "half a rule
    /// reached the answer" structurally impossible rather than merely unlikely.</summary>
    public Guid? AtomicGroupId { get; set; }

    public int? AtomicGroupSequence { get; set; }

    public int? AtomicGroupTotal { get; set; }

    /// <summary>JSON array of floats, e.g. "[0.012,-0.34,...]", L2-normalized. Null until the
    /// embedding provider has run - a chunk can exist (freshly split from the article) slightly
    /// before it is embedded.
    ///
    /// Still JSON text rather than SQL Server 2025's native VECTOR(1536). The native column is what
    /// SqlServerVectorStore reads and is added alongside this one where the server supports it; this
    /// column remains both the fallback store for an older SQL Server and the human-inspectable copy.
    /// See IVectorStore for which of the two is live on a given deployment.</summary>
    public string? Embedding { get; set; }

    /// <summary>Which IEmbeddingService produced the current <see cref="Embedding"/> - "Simulated"/
    /// "OpenAI"/"Google", from IEmbeddingService.ProviderName at embed time. Null until embedded.
    /// Exists so a chunk embedded under a stand-in provider doesn't look identical, in the admin UI,
    /// to one embedded for real.</summary>
    public string? EmbeddingProvider { get; set; }

    /// <summary>The specific model within <see cref="EmbeddingProvider"/> - e.g.
    /// "text-embedding-3-small". Null until embedded.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Dimension count of the stored vector. Written at embed time rather than inferred, so
    /// a provider or model change that alters dimensionality is detectable without parsing every
    /// vector - which is what a re-index needs to know before it starts, not after.</summary>
    public int? EmbeddingDimensions { get; set; }

    public int TokenCount { get; set; }

    /// <summary>Snapshot of <see cref="KnowledgeBaseArticle.VersionNumber"/> at embedding time - lets
    /// a reindex job detect chunks embedded from a since-edited article version without re-reading
    /// every article.</summary>
    public int EmbeddedFromArticleVersion { get; set; }

    /// <summary>False while a re-index builds a replacement set. Only active chunks are retrievable,
    /// which is what makes the ingestion swap atomic from a reader's point of view: the new set is
    /// built inactive, then one transaction flips the old set off and the new set on.</summary>
    public bool IsActive { get; set; } = true;

    // ── Denormalized from the article (written ONLY by ingestion - see the type doc comment) ──

    public int AuthorityRank { get; set; }

    public ProductModule? ProductModule { get; set; }

    public KnowledgeSourceType SourceType { get; set; } = KnowledgeSourceType.AdminConfiguredArticle;

    public string LanguageCode { get; set; } = "en";

    public string? CountryCode { get; set; }

    /// <summary>The article's AppliesToVersionMin/Max, normalized by SemanticVersion.ToNumeric so the
    /// retrieval filter compares integers. NULL means unbounded on that side.
    ///
    /// Stored numerically rather than compared as strings because "2.10.0" sorts BELOW "2.9.0" as
    /// text, which would silently exclude an article from exactly the releases its bound was written
    /// to cover. Computed during ingestion, not at query time - a per-row string parse in the hot
    /// path would also defeat the covering index.</summary>
    public long? VersionMinNumeric { get; set; }

    public long? VersionMaxNumeric { get; set; }

    public KnowledgeArticleStatus ArticleStatus { get; set; } = KnowledgeArticleStatus.Draft;

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public bool IsCurrentArticleVersion { get; set; }

    /// <summary><see cref="ContextHeader"/> + <see cref="ChunkText"/> + the article's Keywords, with
    /// the keywords repeated twice - which is how the 2x keyword field boost is achieved without
    /// maintaining a second full-text index. Written by ingestion, read by the keyword leg.</summary>
    public string SearchText { get; set; } = string.Empty;
}
