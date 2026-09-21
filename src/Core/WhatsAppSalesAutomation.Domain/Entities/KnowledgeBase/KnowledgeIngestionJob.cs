using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>
/// One chunk-embed-index pass over one version of one article (G.6).
///
/// Unique per (ArticleId, ArticleVersionNumber): embedding costs money, so queueing the same version
/// twice - a double-click, a retried request - returns this row rather than paying twice. A Failed
/// job is the exception and is re-run in place, resuming from <see cref="LastCompletedChunkIndex"/>.
/// </summary>
public class KnowledgeIngestionJob : BaseEntity, ITenantScopedOrGlobal
{
    /// <summary>Copied from the article; NULL for a GLOBAL one.</summary>
    public Guid? TenantId { get; set; }

    public Guid ArticleId { get; set; }

    public int ArticleVersionNumber { get; set; }

    public KnowledgeIngestionState State { get; set; } = KnowledgeIngestionState.Queued;

    /// <summary>Short machine-readable reason for Failed/Rejected, e.g. "InjectionBlocked",
    /// "EmbeddingProviderFailed". Free-text detail goes in <see cref="Detail"/>.</summary>
    public string? ReasonCode { get; set; }

    public string? Detail { get; set; }

    /// <summary>Set when the injection scan flagged (not blocked) the content. Indexing waits at
    /// AwaitingApproval until a human allows it.</summary>
    public bool RequiresSecurityReview { get; set; }

    /// <summary>The findings that caused it, as JSON, so a reviewer sees the sentences and not just a
    /// flag.</summary>
    public string? SecurityFindingsJson { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>Highest chunk index whose vector is safely staged. Resume starts after it, so a
    /// failure at chunk 480 of 500 does not re-pay for the first 480.</summary>
    public int LastCompletedChunkIndex { get; set; } = -1;

    public string? EmbeddingProvider { get; set; }

    public string? EmbeddingModel { get; set; }

    /// <summary>Chunk quality metrics (H.8) as JSON.</summary>
    public string? MetricsJson { get; set; }

    /// <summary>Non-fatal observations from post-index verification - a duplicate, a failed smoke
    /// retrieval. Warnings, not failures: the index is built and correct, and a human decides.</summary>
    public string? VerificationNotes { get; set; }

    public int Attempts { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
