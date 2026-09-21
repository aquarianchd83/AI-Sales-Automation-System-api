using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.KnowledgeBase;

/// <summary>
/// Canonical, human-authored/approved source text the AI is allowed to ground replies in.
///
/// Only <see cref="Status"/> == Published AND <see cref="IsCurrentVersion"/> AND within the
/// <see cref="EffectiveFrom"/>/<see cref="EffectiveTo"/> window is eligible for autonomous support
/// answering - see <c>IKnowledgeRetrievalService</c>'s hard filter, which applies all three in SQL
/// rather than in memory so an ineligible article is never even a candidate.
///
/// Implements <see cref="ITenantScopedOrGlobal"/>, not <see cref="ITenantOwned"/>: a NULL
/// <see cref="TenantId"/> means GLOBAL (platform-owned, readable by every tenant), which the ordinary
/// tenant-owned query filter cannot express. Implementing both would be a silent isolation bug -
/// <c>ApplicationDbContext</c> refuses to build a model where any entity does.
/// </summary>
public class KnowledgeBaseArticle : BaseEntity, ISoftDelete, ITenantScopedOrGlobal
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>NULL = GLOBAL (platform knowledge). Non-null = that tenant's private knowledge.
    ///
    /// Setting this also updates <see cref="TenantScope"/>. That is not a convenience: the last
    /// writer of this property is usually TenantStampingSaveChangesInterceptor, which stamps an
    /// unattributed insert with the current tenant and has no idea a denormalized scope column
    /// exists. Leaving the two to be set independently meant every such insert hit
    /// CK_KBArticles_ScopeMatchesTenant at save time - a correct constraint catching a mismatch the
    /// object model had allowed. Deriving it here makes the mismatch unrepresentable, and leaves the
    /// constraint as the backstop it was meant to be rather than a tripwire on the normal path.</summary>
    public Guid? TenantId
    {
        get => _tenantId;
        set
        {
            _tenantId = value;

            TenantScope = value is null ? TenantKnowledgeScope.Global : TenantKnowledgeScope.Tenant;

            // BR-3, applied at the moment an article becomes tenant-scoped rather than trusted to
            // every caller that might get it there. The interceptor that stamps an unattributed
            // insert knows nothing about authority ranks, so without this a tenant article could
            // arrive at the database carrying platform authority and be rejected by
            // CK_KBArticles_TenantAuthority - correct, but as a save-time exception rather than as
            // the rule working. Capping (rather than throwing) matches
            // KnowledgeAuthority.RankFor(type, isGlobal), which is the same rule at the other end.
            if (value is not null && AuthorityRank > KnowledgeAuthority.TenantMaxRank)
                AuthorityRank = KnowledgeAuthority.TenantMaxRank;
        }
    }

    private Guid? _tenantId;

    /// <summary>Denormalized from <see cref="TenantId"/>'s nullability - see
    /// <see cref="TenantKnowledgeScope"/> for why it is stored rather than computed. Never set
    /// directly; it follows <see cref="TenantId"/>. EF Core writes it through the private setter when
    /// materializing a row, and whichever of the two it happens to set second, the result agrees -
    /// the database guarantees the pair was consistent when it was written.</summary>
    ///
    /// The default is Global because the default TenantId is null. The setter above only runs when
    /// TenantId is ASSIGNED, so a new platform article - which never assigns it - would otherwise start
    /// as "tenant-scoped with no tenant" and be refused by CK_KBArticles_ScopeMatchesTenant.
    public TenantKnowledgeScope TenantScope { get; private set; } = TenantKnowledgeScope.Global;

    /// <summary>Stable, human-readable identity that survives versioning. Every version of
    /// "refund-policy-india" shares this key; only one of them has <see cref="IsCurrentVersion"/>
    /// true. Unique per (TenantId, ArticleKey, LanguageCode).
    ///
    /// This - not <see cref="BaseEntity.Id"/> - is what a citation in an AI answer or an escalation
    /// packet refers to when it names "the article", so a citation stays meaningful across a rewrite.</summary>
    public string ArticleKey { get; set; } = string.Empty;

    // ── Content ──────────────────────────────────────────────────────────────

    public string Title { get; set; } = string.Empty;

    /// <summary>Markdown. Headings drive structure-aware chunking, so authors are asked to use them
    /// meaningfully rather than for visual weight - see the authoring template in §E.5.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>SHA-256 of normalized <see cref="Content"/>, lowercase hex. Powers exact-duplicate
    /// detection on save, and lets ingestion skip re-embedding when an "edit" changed only metadata -
    /// which is the difference between a cheap save and a paid round-trip to the embedding provider.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Author-supplied search terms, semicolon-separated, e.g.
    /// "credit;credits;balance;quota;AI usage". Fed into the keyword leg of hybrid retrieval at a 2x
    /// field boost - this is how a tenant's own phrasing ("quota khatam") reaches an article whose
    /// prose never uses that word, and it is the main way Hindi keyword search works at all given
    /// SQL Server's weak Hindi word breaker (§J.6).</summary>
    public string? Keywords { get; set; }

    // ── Classification ───────────────────────────────────────────────────────

    public KnowledgeCategory Category { get; set; } = KnowledgeCategory.GettingStarted;

    /// <summary>Free-text refinement under <see cref="Category"/>. Not an enum on purpose -
    /// subcategories proliferate with the product and are not worth a migration each time. This is
    /// also where the pre-Phase-6 free-text Category values were preserved by the backfill.</summary>
    public string? SubCategory { get; set; }

    /// <summary>Which platform module this article is about. NULL = cross-cutting (e.g. a billing
    /// policy that is not module-specific). Matched against the ticket's detected module for a
    /// retrieval boost, never as a hard filter - an article about the wrong module is less relevant,
    /// not ineligible.</summary>
    public ProductModule? ProductModule { get; set; }

    // ── Authority ────────────────────────────────────────────────────────────

    public KnowledgeSourceType SourceType { get; set; } = KnowledgeSourceType.AdminConfiguredArticle;

    /// <summary>0-100, denormalized from <see cref="SourceType"/> at save time through
    /// <c>KnowledgeAuthority.RankFor</c> so retrieval never needs a lookup-table join. A SuperAdmin
    /// may nudge a GLOBAL article by up to +/-10 (audited). The CK_KBArticles_TenantAuthority check
    /// constraint caps tenant-scoped articles at 30 - that constraint IS business rule BR-3, and it
    /// holds whether or not the application remembered to apply it.</summary>
    public int AuthorityRank { get; set; }

    /// <summary>0-100 manual tiebreak WITHIN the same <see cref="AuthorityRank"/>. Never crosses
    /// authority tiers, by construction: it is only consulted after rank has already decided. Use it
    /// to make "the good FAQ" beat "the stub FAQ", not to promote tenant content.</summary>
    public int Priority { get; set; } = 50;

    // ── Applicability ────────────────────────────────────────────────────────

    /// <summary>ISO 3166-1 alpha-2, e.g. "IN", "AE". NULL = applies to every country. Matched against
    /// the tenant's registered country.</summary>
    public string? CountryCode { get; set; }

    /// <summary>BCP-47, "en" or "hi" in Phase 6. Not nullable: every article is written in exactly one
    /// language. A translated article is a SEPARATE row sharing the same <see cref="ArticleKey"/>.</summary>
    public string LanguageCode { get; set; } = "en";

    /// <summary>Inclusive semantic-version bounds for the platform release this article describes.
    /// NULL/NULL = version-agnostic. Compared through a normalized numeric form, never a string
    /// compare - "2.10.0" must sort above "2.9.0".</summary>
    public string? AppliesToVersionMin { get; set; }

    public string? AppliesToVersionMax { get; set; }

    /// <summary>When this article's content becomes true. Defaults to publish time. A policy change
    /// announced today but effective next month is authored NOW with a future value here, and
    /// retrieval simply will not see it until then - no scheduled job required, which means no
    /// scheduled job to fail.</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>When it stops being true. NULL = open-ended. Once passed, the article is invisible to
    /// retrieval immediately (a query-time check, not a job), and the expiry job moves it to
    /// Deprecated within the hour so the admin UI agrees with what retrieval is already doing.</summary>
    public DateTime? EffectiveTo { get; set; }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public KnowledgeArticleStatus Status { get; set; } = KnowledgeArticleStatus.Draft;

    /// <summary>Monotonic per <see cref="ArticleKey"/>. Incremented on every publish, not on every
    /// save - a Draft edited ten times still becomes exactly one new version when published.</summary>
    public int VersionNumber { get; set; } = 1;

    /// <summary>Exactly one row per (TenantId, ArticleKey, LanguageCode) may have this true, enforced
    /// by the UX_KBArticles_CurrentVersion filtered unique index. Retrieval filters on it, so a
    /// rollback is a two-row flag swap rather than a data migration.</summary>
    public bool IsCurrentVersion { get; set; } = true;

    /// <summary>The article this one replaced, for lineage. NULL for a first version.</summary>
    public Guid? SupersedesArticleId { get; set; }

    /// <summary>When a human must next re-confirm this article is still true. Set from a per-SourceType
    /// default at approval - see <c>KnowledgeAuthority.ReviewIntervalDays</c>. Overdue articles are
    /// NOT removed from retrieval, only flagged loudly in the admin queue: silently dropping a
    /// still-correct policy would be worse than serving a slightly stale one.</summary>
    public DateTime? ReviewDueAt { get; set; }

    // ── Governance ───────────────────────────────────────────────────────────

    public Guid? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public Guid? PublishedBy { get; set; }

    public DateTime? PublishedAt { get; set; }

    /// <summary>Who review reminders go to.</summary>
    public Guid? OwnerUserId { get; set; }

    public Guid? LastUpdatedBy { get; set; }

    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>Why this article was deprecated or archived. Shown in the admin UI and carried into
    /// the lineage trail. Required when moving to Deprecated - "this is no longer true" is not useful
    /// to the next person without "because".</summary>
    public string? LifecycleNote { get; set; }

    // ── Soft delete ──────────────────────────────────────────────────────────

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────

    public ICollection<KnowledgeBaseChunk> Chunks { get; set; } = new List<KnowledgeBaseChunk>();

    /// <summary>Which AI chat models this article is currently eligible for - see
    /// KnowledgeBaseArticleModelPublication's doc comment.</summary>
    public ICollection<KnowledgeBaseArticleModelPublication> ModelPublications { get; set; }
        = new List<KnowledgeBaseArticleModelPublication>();

    /// <summary>True when this article is eligible to be returned by support retrieval right now.
    /// Mirrors the hard SQL filter so the two can be checked against each other, and exists for the
    /// admin UI and for tests that want to assert eligibility on a materialized entity rather than
    /// through a query. Not a substitute for the SQL filter: filtering in memory would mean an
    /// ineligible article had already been fetched.</summary>
    public bool IsRetrievable(DateTime asOfUtc) =>
        Status == KnowledgeArticleStatus.Published
        && IsCurrentVersion
        && !IsDeleted
        && EffectiveFrom <= asOfUtc
        && (EffectiveTo == null || EffectiveTo > asOfUtc);
}
