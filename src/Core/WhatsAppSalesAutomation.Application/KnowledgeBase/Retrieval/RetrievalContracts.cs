using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

/// <summary>
/// Support-grade retrieval (§K.8). Distinct from <c>IKnowledgeBaseService</c>, the sales
/// conversation's RAG: the two answer different questions over different corpora at different risk
/// tolerances, and sharing one method would mean one set of thresholds for both - for which there is
/// no single right pair.
/// </summary>
public interface IKnowledgeRetrievalService
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(KnowledgeRetrievalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything retrieval needs, stated explicitly. The one thing read from ambient state is the
/// tenant (via ITenantContext, which the database query filter also reads) - so a caller cannot
/// retrieve with a stale country or the wrong platform version by forgetting to pass it.
/// </summary>
public sealed record KnowledgeRetrievalRequest(
    string RawQuery,
    IReadOnlyList<string> ConversationContext,
    SupportIntent? DetectedIntent,
    ProductModule? DetectedModule,
    string? TenantCountry,
    string TicketLanguage,
    string? TenantPlatformVersion,
    string? SubscriptionPlanCode = null,
    Guid? TicketId = null,
    /// <summary>Skip reading the result cache. For the admin simulator: right after publishing an
    /// article, an answer up to a minute old is the wrong thing to show someone checking whether the
    /// publish worked. Results are still written, so this only affects what is read.</summary>
    bool BypassCache = false);

public sealed record RetrievedEvidence(
    Guid ChunkId,
    Guid ArticleId,
    string ArticleKey,
    int ArticleVersionNumber,
    string Title,
    KnowledgeSourceType SourceType,
    int AuthorityRank,
    string ContextHeader,
    string ChunkText,
    double VectorScore,
    double KeywordScore,
    double FusedScore,
    double RerankScore,
    int Rank,
    Guid? AtomicGroupId,
    /// <summary>True when this chunk was not selected on score but pulled in because another chunk of
    /// its atomic group was - see AtomicGroupExpansion.</summary>
    bool IsGroupExpansion);

/// <summary>Why retrieval returned what it returned - the artefact that answers "why did the AI say
/// that?". Persisted on the agent run and shown in the retrieval inspector.</summary>
public sealed record RetrievalDiagnostics(
    string NormalizedQuery,
    IReadOnlyList<string> ExpandedQueries,
    /// <summary>Distinct chunks that reached fusion from either leg - the candidate set, not the whole
    /// eligible corpus, which counting would cost a full scan to no purpose.</summary>
    int CandidateCount,
    int VectorHitCount,
    int KeywordHitCount,
    int FusedCount,
    int RerankedCount,
    double TopScore,
    int SupportingChunkCount,
    bool EvidenceGatePassed,
    string? GateFailureReason,
    int RetrievalLatencyMs,
    bool EmbeddingCacheHit,
    bool ResultCacheHit,
    RetrievalMode Mode,
    string VectorStore,
    string KeywordStore,
    string Reranker,
    /// <summary>Rank change between fusion order and final order, averaged. Persistently high means
    /// fusion keeps offering the wrong candidates and the reranker keeps correcting it - a signal to
    /// retune the fusion weights (§L.5).</summary>
    double RankChurn,
    /// <summary>Top score minus second score. A small spread on a passing gate is an ambiguous
    /// question, and a candidate for clarification rather than an answer.</summary>
    double ScoreSpread);

public sealed record KnowledgeRetrievalResult(
    IReadOnlyList<RetrievedEvidence> Evidence,
    RetrievalDiagnostics Diagnostics);

public sealed record KeywordHit(Guid ChunkId, Guid ArticleId, double Score);

/// <summary>
/// The keyword half of hybrid retrieval. Vector search is semantic; support questions often turn on an
/// exact token - an error code, a template name, a version - and an embedding model sees 131047 and
/// 131026 as nearly the same thing. This leg is what does not.
///
/// Implementations apply the SAME hard filter as the vector leg, in the query, for the same reason.
/// </summary>
public interface IKeywordSearchStore
{
    string ProviderName { get; }

    Task<IReadOnlyList<KeywordHit>> SearchAsync(
        string queryText, RetrievalFilter filter, int topN, CancellationToken cancellationToken = default);
}

/// <summary>Scores candidate passages for how well they ANSWER a question, which a bi-encoder cannot
/// judge - it never sees the query and the passage together (§L.1).</summary>
public interface IReranker
{
    string ProviderName { get; }

    /// <summary>Scores aligned with <paramref name="documents"/>, each 0..1. Null when the reranker is
    /// unavailable - not configured, timed out, erroring, or its circuit is open. Null, never
    /// fabricated scores: the caller switches to the stricter fusion-only gate instead.</summary>
    Task<IReadOnlyList<double>?> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default);
}

/// <summary>Short-lived cache for embeddings and results. Behind an interface so the Application layer
/// carries no caching library; every key is built by <see cref="RetrievalCacheKeys"/>.</summary>
public interface IRetrievalCache
{
    bool TryGet<T>(string key, out T? value);

    void Set<T>(string key, T value, TimeSpan ttl);
}

/// <summary>
/// Cache keys always include the tenant (§P.3).
///
/// A result cache keyed only by the query would serve tenant A's private-article answer to tenant B
/// asking the same question - a cross-tenant leak that needs no bug in the filters at all. Putting the
/// tenant in the key makes that structurally impossible, at the cost of a lower hit rate that is
/// worth paying.
/// </summary>
public static class RetrievalCacheKeys
{
    public static string Tenant(Guid? tenantId) => tenantId?.ToString("N") ?? "platform";

    public static string Embedding(Guid? tenantId, string provider, string model, string text) =>
        $"kb:emb:{Tenant(tenantId)}:{provider}:{model}:{Hash(text)}";

    public static string Result(Guid? tenantId, string requestFingerprint) =>
        $"kb:res:{Tenant(tenantId)}:{Hash(requestFingerprint)}";

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..32];
}
