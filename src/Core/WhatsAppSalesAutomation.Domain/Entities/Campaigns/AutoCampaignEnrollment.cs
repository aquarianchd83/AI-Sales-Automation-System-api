using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Campaigns;

/// <summary>
/// LEGACY - no longer written. Replaced by LeadDiscoveryExecution and its per-step history; kept so the
/// enrollments recorded before that change stay readable through GET lead-discovery/auto-campaign-history.
///
/// One outcome of running a newly discovered customer through auto-campaign enrollment - the audit
/// record behind LeadDiscoveryProfile.AutoCampaignEnabled, and the idempotency guard that keeps a
/// repeated lead-discovery run from enrolling the same customer into the same source campaign twice.
/// Written exactly once per (TenantId, SourceCampaignId, CustomerId) that reaches Started - see the
/// unique filtered index in AutoCampaignEnrollmentConfiguration.
///
/// Also doubles as the "today's execution campaign for this source campaign" lookup:
/// The former AutoCampaignEnrollmentService reused the newest Started row for (TenantId, SourceCampaignId,
/// ExecutionDateLocal) instead of cloning a fresh execution campaign per discovered customer, so every
/// customer discovered the same day shares one execution campaign.
/// </summary>
public class AutoCampaignEnrollment : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The LeadDiscoveryProfile.SourceCampaignId this row was processed against. Null only for
    /// a Skipped row written before a source campaign was even configured. A plain Guid, not a foreign
    /// key - same convention as DiscoveredLead.CustomerId.</summary>
    public Guid? SourceCampaignId { get; set; }

    /// <summary>The cloned campaign the customer was (or would have been) attached to. Null for a Skipped
    /// row that never reached cloning, or a Failed row that failed before the clone was saved.</summary>
    public Guid? ExecutionCampaignId { get; set; }

    public Guid DiscoveredLeadId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The tenant-local calendar date (ITenantTimeZoneProvider), midnight, this row was
    /// processed on - what same-day execution-campaign reuse and the generated
    /// "&lt;Source Campaign Name&gt;-&lt;date&gt;" name are both keyed on.</summary>
    public DateTime ExecutionDateLocal { get; set; }

    public AutoCampaignEnrollmentStatus Status { get; set; }

    /// <summary>Why, for a Skipped or Failed row - e.g. "Auto campaign disabled", "No source campaign
    /// configured", "Source campaign 'X' is Stopped", or the exception message from a failed clone/start.
    /// Null for Started.</summary>
    public string? Reason { get; set; }
}
