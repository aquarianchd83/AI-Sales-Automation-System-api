using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

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

    /// <summary>What the tenant says about this search - what they sell and who they are after. Not used by the run
    /// itself: it is what "AI suggest" draws <see cref="Keywords"/> from (with the packages the tenant sells).</summary>
    public string? Description { get; set; }

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

    /// <summary>When true, and <see cref="SourceCampaignId"/> is set, Auto-Campaign is configured: each
    /// execution that creates new customers gets one campaign per processing date, named
    /// "&lt;Source Campaign Name&gt; - YYYY-MM-DD", built from the source campaign's templates and with those
    /// customers mapped to it - see LeadDiscoveryRunService. False by default.</summary>
    public bool AutoCampaignEnabled { get; set; }

    /// <summary>The referred campaign - the whole Auto-Campaign configuration: its steps are the templates
    /// (sequence, delays, message text and variables, media). Its own schedule is never used - see
    /// <see cref="AutoCampaignStartMode"/>. Only its own steps are ever used; nothing else from the
    /// tenant's template library. A plain Guid, not a foreign key - same cross-aggregate-reference
    /// convention as <see cref="DiscoveredLead.CustomerId"/>. Required whenever
    /// <see cref="AutoCampaignEnabled"/> is true; re-validated as an existing, non-Stopped campaign every
    /// time it is used.</summary>
    public Guid? SourceCampaignId { get; set; }

    /// <summary>When the campaign an execution generates should start - see LeadDiscoveryRunService.
    /// Immediate by default.</summary>
    public LeadDiscoveryCampaignStartMode AutoCampaignStartMode { get; set; } = LeadDiscoveryCampaignStartMode.Immediate;

    /// <summary>The tenant's own local time of day the generated campaign starts the day after its
    /// processing date - only used, and required, when <see cref="AutoCampaignStartMode"/> is
    /// <see cref="LeadDiscoveryCampaignStartMode.NextDayWithTime"/>.</summary>
    public TimeSpan? AutoCampaignStartTime { get; set; }

    /// <summary>Whether discovered mobile numbers are checked against DND and similar outreach restrictions before a customer
    /// is created. Off by default: when off nothing is submitted for verification, every qualified lead becomes a customer, and the
    /// auto-campaign takes all of them.</summary>
    public bool DndCheckEnabled { get; set; }

    /// <summary>Whether each customer who joins the auto-campaign is sent an SMS inviting them to start a WhatsApp conversation, which
    /// is how their consent is obtained. Off by default: nothing is sent unless the tenant turns it on.</summary>
    public bool ConsentSmsEnabled { get; set; }
}
