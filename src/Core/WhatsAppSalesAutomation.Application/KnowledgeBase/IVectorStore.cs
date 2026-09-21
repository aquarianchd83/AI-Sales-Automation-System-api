using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

/// <summary>
/// The vector-similarity half of hybrid retrieval, behind an interface so the store can change
/// without retrieval changing - see §J.7.
///
/// Two implementations ship: <c>SqlServerVectorStore</c>, which uses SQL Server 2025's native
/// VECTOR type and its index, and <c>JsonColumnVectorStore</c>, which reads the JSON embedding
/// column and scores in the application. Which one is live is decided once at startup by a
/// capability probe, and logged loudly - see <c>IVectorStoreCapability</c>.
/// </summary>
public interface IVectorStore
{
    /// <summary>Which implementation is active, for diagnostics and the retrieval simulate endpoint.
    /// Worth surfacing because the two have very different performance characteristics, and "why is
    /// retrieval slow" should not require reading the startup log.</summary>
    string ProviderName { get; }

    /// <summary>
    /// The top-N chunk ids by vector similarity, AFTER applying the hard metadata filter.
    ///
    /// The filter is part of the contract, not an afterthought: an implementation that retrieved
    /// first and filtered second would pull cross-tenant chunks into the candidate set even if it
    /// dropped them again afterwards, which §P treats as a breach regardless of what the caller does
    /// next. Every implementation applies <paramref name="filter"/> in the query itself.
    /// </summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        RetrievalFilter filter,
        int topN,
        CancellationToken cancellationToken = default);

    /// <summary>Writes vectors for chunks that already exist. Called by ingestion after embedding,
    /// inside the same transaction as the staging swap.</summary>
    Task UpsertAsync(IReadOnlyList<ChunkVector> chunks, CancellationToken cancellationToken = default);

    Task DeleteByArticleAsync(Guid articleId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The hard metadata filter every retrieval runs through (§K.3). A chunk outside this cannot become
/// a candidate under any circumstances.
///
/// A record rather than a list of parameters so that adding a dimension is a compile error at every
/// call site instead of a silently skipped condition - the failure mode for a filter is that it
/// quietly matches more than it should.
/// </summary>
public record RetrievalFilter
{
    /// <summary>The tenant asking. GLOBAL chunks (TenantId NULL) are always eligible in addition to
    /// this tenant's own; null here means a platform caller, who sees GLOBAL only.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Evaluated against EffectiveFrom/EffectiveTo. Passed in rather than read from the
    /// clock inside the query so a simulation can ask "what would we have answered last Tuesday".</summary>
    public DateTime NowUtc { get; init; }

    /// <summary>The tenant's registered country. Chunks with no country are eligible for everyone;
    /// chunks with one are eligible only for a match. Null means the tenant's country is unknown, in
    /// which case only country-agnostic chunks qualify (EC-24).</summary>
    public string? TenantCountryCode { get; init; }

    /// <summary>The ticket's language. English chunks stay eligible alongside it as a fallback, so a
    /// Hindi ticket can still be answered from the English corpus rather than from nothing.</summary>
    public string TicketLanguageCode { get; init; } = "en";

    /// <summary>The tenant's platform version, normalized by <c>SemanticVersion.ToNumeric</c>. Null
    /// skips the version bounds entirely (EC-25) - an unknown version must not silently exclude every
    /// version-bounded article.</summary>
    public long? TenantVersionNumeric { get; init; }

    /// <summary>Restricts to one module when set. Normally null: module is a retrieval BOOST, not a
    /// filter, because an article about the wrong module is less relevant rather than wrong. Exposed
    /// for the admin simulate endpoint, which needs to ask narrower questions than retrieval does.</summary>
    public ProductModule? ProductModule { get; init; }

    /// <summary>The embedding provider and model the QUERY vector came from. Set, a chunk is only a
    /// candidate if its own vector came from the same provider and model.
    ///
    /// Vectors from different models live in unrelated spaces, so cosine between them is not a weak
    /// signal, it is noise that still looks like a number - and it ranks confidently. Comparing them
    /// silently returns a plausible-looking wrong answer instead of nothing, which is why this is a
    /// filter and not a scoring penalty. Null (diagnostics only) skips the check.</summary>
    public string? EmbeddingProvider { get; init; }

    public string? EmbeddingModel { get; init; }

    /// <summary>Only platform-owned (GLOBAL) chunks. Set on the retrieval leg that searches the platform's
    /// own embedding space, so that leg cannot return a tenant's chunks that merely happen to share
    /// it - they are already covered by the tenant's own leg, and returning them twice would only be
    /// noise for fusion to sort out.</summary>
    public bool GlobalOnly { get; init; }

    /// <summary>Restricts to one article. Only used by diagnostics - "why did this article not come
    /// back" is otherwise unanswerable without reading the whole candidate set.</summary>
    public Guid? ArticleId { get; init; }
}

/// <summary>One chunk the vector leg matched, with its similarity in 0..1 (1 = identical direction).
/// Cosine DISTANCE is what SQL Server returns, so implementations convert - a store returning
/// distance here while another returns similarity would invert the ranking with no error.</summary>
public record VectorHit(Guid ChunkId, Guid ArticleId, double Similarity);

/// <summary>A chunk's vector on its way into the store.</summary>
public record ChunkVector(Guid ChunkId, ReadOnlyMemory<float> Embedding, string Provider, string Model);
