namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What KIND of truth an article is. Replaces the two-value KnowledgeBaseSourceType, which
/// recorded how content arrived (Manual/Upload) rather than how much it should be trusted.
///
/// This drives <c>KnowledgeAuthority.RankFor</c> and therefore drives conflict resolution: when a
/// tenant's own article and a platform policy disagree, the winner is decided by this, not by which
/// one retrieval happened to rank first.
///
/// Numbered with no gaps but persisted as a string (see KnowledgeBaseArticleConfiguration), so the
/// numeric values are not load-bearing and a new source type can be inserted without renumbering
/// anything under existing rows.</summary>
public enum KnowledgeSourceType
{
    PlatformPolicy = 0,
    LegalCompliance = 1,
    BillingRule = 2,
    RefundCancellationPolicy = 3,
    AiUsageCreditRule = 4,
    WhatsAppPolicy = 5,
    LeadDiscoveryRule = 6,
    ProductDocumentation = 7,
    FeatureModuleDocumentation = 8,
    ApprovedFaq = 9,
    TroubleshootingGuide = 10,
    KnownIssue = 11,
    ReleaseChangeNote = 12,

    /// <summary>The only source type a tenant may author, and the one the pre-Phase-6 Manual/Upload
    /// rows migrate to. Rank 30, which is <c>KnowledgeAuthority.TenantMaxRank</c>.</summary>
    AdminConfiguredArticle = 13,

    HistoricalDocumentation = 14
}
