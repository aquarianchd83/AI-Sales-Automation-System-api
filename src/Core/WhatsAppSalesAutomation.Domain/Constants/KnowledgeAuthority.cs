using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Constants;

/// <summary>
/// The single source of truth for how authoritative each kind of knowledge is.
///
/// Lives in Domain/Constants next to <see cref="QualificationDefaults"/> because it is a business
/// rule, not configuration: changing it changes which article wins a policy conflict, and that is a
/// decision that belongs in a reviewed commit rather than in appsettings.json where an operator could
/// quietly promote tenant content above platform policy.
/// </summary>
public static class KnowledgeAuthority
{
    /// <summary>The highest <c>AuthorityRank</c> a tenant-scoped article may hold (BR-3), also
    /// enforced as the CK_KBArticles_TenantAuthority database check constraint. Two layers because
    /// the application rule protects the good path and the constraint protects every other one.</summary>
    public const int TenantMaxRank = 30;

    /// <summary>How far a SuperAdmin may nudge a GLOBAL article's rank away from its source type's
    /// default. Bounded so a nudge can reorder articles within a tier or reach an adjacent one, but
    /// cannot turn a release note into a legal policy.</summary>
    public const int MaxManualRankAdjustment = 10;

    private static readonly IReadOnlyDictionary<KnowledgeSourceType, int> Ranks =
        new Dictionary<KnowledgeSourceType, int>
        {
            [KnowledgeSourceType.PlatformPolicy] = 100,
            [KnowledgeSourceType.LegalCompliance] = 100,
            [KnowledgeSourceType.BillingRule] = 90,
            [KnowledgeSourceType.RefundCancellationPolicy] = 90,
            [KnowledgeSourceType.AiUsageCreditRule] = 90,
            [KnowledgeSourceType.WhatsAppPolicy] = 80,
            [KnowledgeSourceType.LeadDiscoveryRule] = 80,
            [KnowledgeSourceType.ProductDocumentation] = 70,
            [KnowledgeSourceType.FeatureModuleDocumentation] = 70,
            [KnowledgeSourceType.ApprovedFaq] = 60,
            [KnowledgeSourceType.TroubleshootingGuide] = 50,
            [KnowledgeSourceType.KnownIssue] = 45,
            [KnowledgeSourceType.ReleaseChangeNote] = 40,
            [KnowledgeSourceType.AdminConfiguredArticle] = 30,
            [KnowledgeSourceType.HistoricalDocumentation] = 10
        };

    /// <summary>Source types a tenant may never author. Each one is a statement ABOUT the platform,
    /// and letting a tenant write one would let tenant content claim platform authority - which is
    /// the same breach as raising its rank, reached by a different door.</summary>
    public static readonly IReadOnlySet<KnowledgeSourceType> GlobalOnly = new HashSet<KnowledgeSourceType>
    {
        KnowledgeSourceType.PlatformPolicy,
        KnowledgeSourceType.LegalCompliance,
        KnowledgeSourceType.BillingRule,
        KnowledgeSourceType.RefundCancellationPolicy,
        KnowledgeSourceType.AiUsageCreditRule,
        KnowledgeSourceType.WhatsAppPolicy,
        KnowledgeSourceType.LeadDiscoveryRule,
        KnowledgeSourceType.ProductDocumentation,
        KnowledgeSourceType.FeatureModuleDocumentation,
        KnowledgeSourceType.KnownIssue,
        KnowledgeSourceType.ReleaseChangeNote
    };

    /// <summary>Throws for an undefined enum value rather than returning a default. A source type
    /// added to the enum but not to the map would otherwise silently rank 0, i.e. below
    /// HistoricalDocumentation - the safest-looking wrong answer and the hardest to notice.</summary>
    public static int RankFor(KnowledgeSourceType type) =>
        Ranks.TryGetValue(type, out var rank)
            ? rank
            : throw new ArgumentOutOfRangeException(
                nameof(type), type,
                $"{nameof(KnowledgeAuthority)} has no rank for this source type. Add it to the Ranks map - " +
                "defaulting would silently rank it below every other kind of knowledge.");

    /// <summary>The rank an article of this type and scope actually gets, with the tenant cap applied.
    /// Ingestion and the article service both denormalize through here rather than reading
    /// <see cref="RankFor"/> directly, so the cap cannot be forgotten at one call site.</summary>
    public static int RankFor(KnowledgeSourceType type, bool isGlobal)
    {
        var rank = RankFor(type);
        return isGlobal ? rank : Math.Min(rank, TenantMaxRank);
    }

    /// <summary>Whether a tenant is allowed to author this source type at all.</summary>
    public static bool IsAuthorableByTenant(KnowledgeSourceType type) => !GlobalOnly.Contains(type);

    /// <summary>Default review interval in days, by source type - the basis for an article's
    /// ReviewDueAt at approval time. A KnownIssue goes stale in a month; an FAQ can sit for a year.</summary>
    public static int ReviewIntervalDays(KnowledgeSourceType type) => type switch
    {
        KnowledgeSourceType.KnownIssue => 30,
        KnowledgeSourceType.ReleaseChangeNote => 90,
        KnowledgeSourceType.PlatformPolicy or KnowledgeSourceType.LegalCompliance
            or KnowledgeSourceType.RefundCancellationPolicy or KnowledgeSourceType.BillingRule => 180,
        KnowledgeSourceType.ProductDocumentation or KnowledgeSourceType.FeatureModuleDocumentation
            or KnowledgeSourceType.WhatsAppPolicy or KnowledgeSourceType.AiUsageCreditRule
            or KnowledgeSourceType.LeadDiscoveryRule => 180,
        _ => 365
    };
}
