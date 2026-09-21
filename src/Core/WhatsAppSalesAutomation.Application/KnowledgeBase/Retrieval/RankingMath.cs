using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

/// <summary>One ranked list feeding fusion: a leg of one query, best first.</summary>
public sealed record RankedList(string Name, double Weight, IReadOnlyList<Guid> ChunkIds);

/// <summary>
/// Reciprocal Rank Fusion (§K.5).
///
/// RRF looks only at RANK, never at score, and that is the reason it is used. Cosine similarity
/// (0..1) and a full-text rank (0..1000, corpus-dependent) are not on the same scale, and every
/// attempt to normalize them together breaks when the corpus changes. Ranks are comparable by
/// construction.
/// </summary>
public static class RankFusion
{
    /// <summary>Fused score per chunk, highest first. <paramref name="k"/> damps the gap between
    /// rank 1 and rank 2 - 60, from the original paper, is deliberately untuned.</summary>
    public static IReadOnlyList<(Guid ChunkId, double Score)> Fuse(IReadOnlyList<RankedList> lists, int k)
    {
        var scores = new Dictionary<Guid, double>();

        foreach (var list in lists)
        {
            for (var i = 0; i < list.ChunkIds.Count; i++)
            {
                var rank = i + 1;
                scores[list.ChunkIds[i]] = scores.GetValueOrDefault(list.ChunkIds[i]) + list.Weight / (k + rank);
            }
        }

        return scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>The best score a chunk could possibly have: rank 1 in every list. Dividing by it puts
    /// fused scores on 0..1, which is what the fusion-only evidence gate compares against.</summary>
    public static double MaxPossible(IReadOnlyList<RankedList> lists, int k) =>
        lists.Where(l => l.ChunkIds.Count > 0).Sum(l => l.Weight / (k + 1));
}

/// <summary>
/// Deterministic pre-rerank boosts (§K.6). They improve ORDER; they never filter.
///
/// No multiplier is zero, on purpose. Filtering already happened, in SQL, and is binary. A boost
/// small enough to behave like a filter would make "why did this article not come back" unanswerable,
/// because the article would be present, scored, and quietly buried.
/// </summary>
public static class MetadataBooster
{
    public static double Multiplier(
        int authorityRank, ProductModule? chunkModule, ProductModule? detectedModule,
        DateTime? publishedAt, DateTime nowUtc) =>
        Authority(authorityRank) * Module(chunkModule, detectedModule) * Recency(publishedAt, nowUtc);

    public static double Authority(int rank) => rank switch
    {
        >= 90 => 1.35,
        >= 70 => 1.20,
        >= 40 => 1.05,
        _ => 1.00
    };

    public static double Module(ProductModule? chunkModule, ProductModule? detectedModule)
    {
        if (detectedModule is null || chunkModule is null)
            return 1.00;   // nothing to compare, or a cross-cutting article

        return chunkModule == detectedModule ? 1.25 : 0.80;
    }

    public static double Recency(DateTime? publishedAt, DateTime nowUtc)
    {
        if (publishedAt is not { } published)
            return 1.00;

        var age = (nowUtc - published).TotalDays;
        return age <= 90 ? 1.10 : age <= 365 ? 1.00 : 0.95;
    }

    /// <summary>The final ordering after reranking (§L.4): the reranker's judgement dominates, but
    /// authority - which a reranker cannot know - is kept as a quarter of the score, so that between
    /// two equally relevant passages the platform policy beats the tenant's note.</summary>
    public static double Final(double rerankScore, double normalizedPreRank) =>
        0.75 * rerankScore + 0.25 * normalizedPreRank;
}

public enum RetrievalMode
{
    /// <summary>Cross-encoder reranking succeeded.</summary>
    Reranked = 0,

    /// <summary>The reranker was unavailable; ordering is fusion plus boosts only, and the gate is
    /// stricter to compensate.</summary>
    FusionOnly = 1
}

public sealed record EvidenceGateResult(bool Passed, double TopScore, int SupportingCount, string? FailureReason);

/// <summary>
/// The evidence gate (§K.7, §S.2): is there enough to answer, or should the agent ask or escalate?
///
/// Its failure mode is the point of the whole design. When it says no, the agent clarifies or hands
/// over; it never answers "as best it can". A support bot that always produces something is worse
/// than no bot, because the something is sometimes confidently wrong.
/// </summary>
public static class EvidenceGate
{
    /// <summary>Fusion-only thresholds (§L.2). Stricter because without a reranker nothing has
    /// checked that the top chunk ANSWERS the question rather than merely resembling it. The system
    /// escalates more when the reranker is down; it does not guess more.</summary>
    public const double FusionOnlyMinTopScore = 0.75;
    public const int FusionOnlyMinSupporting = 3;

    public static EvidenceGateResult Evaluate(
        IReadOnlyList<(double Score, int AuthorityRank)> chunksBestFirst,
        SupportRagOptions options,
        RetrievalMode mode)
    {
        if (chunksBestFirst.Count == 0)
            return new EvidenceGateResult(false, 0, 0, "NoEvidence");

        var minTop = mode == RetrievalMode.Reranked ? options.MinTopRerankScore : FusionOnlyMinTopScore;
        var minSupporting = mode == RetrievalMode.Reranked ? options.MinSupportingChunks : FusionOnlyMinSupporting;
        var singleChunkAllowed = mode == RetrievalMode.Reranked;

        var top = chunksBestFirst[0];
        var supporting = chunksBestFirst.Count(c => c.Score >= options.MinSupportingRerankScore);

        if (top.Score < minTop)
            return new EvidenceGateResult(false, top.Score, supporting, "TopScoreBelowThreshold");

        // Two near-equal results from different authority tiers that may say different things: picking
        // a winner here would be a coin flip presented as an answer. Left to conflict resolution.
        if (chunksBestFirst.Count > 1)
        {
            var second = chunksBestFirst[1];
            if (top.Score - second.Score < options.AmbiguityGapThreshold && Tier(top.AuthorityRank) != Tier(second.AuthorityRank))
                return new EvidenceGateResult(false, top.Score, supporting, "AmbiguousAcrossAuthorityTiers");
        }

        if (supporting >= minSupporting)
            return new EvidenceGateResult(true, top.Score, supporting, null);

        // The single-chunk exception: one crisp, high-authority answer. Deliberately hard to reach.
        if (singleChunkAllowed && top.Score >= options.SingleChunkMinScore && top.AuthorityRank >= options.SingleChunkMinAuthority)
            return new EvidenceGateResult(true, top.Score, supporting, null);

        return new EvidenceGateResult(false, top.Score, supporting, "InsufficientSupportingEvidence");
    }

    /// <summary>Authority bands, not exact ranks: 60 and 61 are the same kind of source.</summary>
    internal static int Tier(int rank) => rank >= 90 ? 3 : rank >= 70 ? 2 : rank >= 40 ? 1 : 0;
}
