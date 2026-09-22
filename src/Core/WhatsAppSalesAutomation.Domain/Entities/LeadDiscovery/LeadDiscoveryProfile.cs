using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

/// <summary>
/// What one tenant's lead discovery job looks for - the target profile, where to search, how many new
/// leads one run may add, and the qualification rules a discovered business has to pass. At most one per
/// tenant (unique index on TenantId); no row, or <see cref="IsEnabled"/> false, means that tenant's
/// lead-discovery job does nothing.
///
/// Industry-independent by design: the target is whatever <see cref="TargetBusinessType"/> and
/// <see cref="Keywords"/> say it is. Nothing in the discovery pipeline branches on a business category.
/// </summary>
public class LeadDiscoveryProfile : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public bool IsEnabled { get; set; }

    /// <summary>Free text, e.g. "Eye clinic" or "Computer hardware distributor".</summary>
    public string TargetBusinessType { get; set; } = string.Empty;

    /// <summary>Search terms, combined with each of <see cref="Locations"/>. JSON array column.</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Cities, regions or areas to search, e.g. "Chandigarh" or "Andheri West, Mumbai". JSON array
    /// column.</summary>
    public List<string> Locations { get; set; } = new();

    /// <summary>The most new leads one run may add. Also capped at run time by the tenant's plan
    /// (Plan.MaxLeadDiscoveryBatchSize), so a later downgrade applies without this being re-saved.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Output fields (LeadDiscoveryFields names) a lead must have to qualify, on top of the
    /// business name and source URL every lead always carries. JSON array column.</summary>
    public List<string> RequiredFields { get; set; } = new();

    public bool PhoneRequired { get; set; } = true;

    public bool EmailRequired { get; set; }

    /// <summary>When true, chains, franchises and branches of larger groups do not qualify.</summary>
    public bool IndependentBusiness { get; set; }

    /// <summary>0-100. A lead scoring below this is not saved.</summary>
    public int MinimumLeadScore { get; set; } = 60;

    /// <summary>Any further qualification criteria in plain language, handed to the agent as written. JSON
    /// array column.</summary>
    public List<string> AdditionalCriteria { get; set; } = new();

    /// <summary>When true, and <see cref="SourceCampaignId"/> is set, every customer this profile's
    /// lead-discovery job discovers is automatically cloned into a same-day execution campaign and
    /// enrolled - see AutoCampaignEnrollmentService. False by default: existing tenants keep doing
    /// nothing extra with a freshly discovered customer until they opt into this.</summary>
    public bool AutoCampaignEnabled { get; set; }

    /// <summary>The existing, tenant-owned Campaign to clone content/schedule from when auto-enrolling a
    /// discovered customer. A plain Guid, not a foreign key - same cross-aggregate-reference convention as
    /// <see cref="DiscoveredLead.CustomerId"/>. Required whenever <see cref="AutoCampaignEnabled"/> is true;
    /// AutoCampaignEnrollmentService re-validates it is still an existing, non-Stopped campaign at
    /// enrollment time rather than trusting it stays valid forever.</summary>
    public Guid? SourceCampaignId { get; set; }
}
