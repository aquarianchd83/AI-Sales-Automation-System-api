using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

/// <summary>
/// One attempt at processing a tenant's lead discovery profile - the unit of Lead Discovery History. Its
/// <see cref="BaseEntity.Id"/> is the Execution ID.
///
/// Every attempt gets its own row, including one that was blocked by another execution's lock and did
/// nothing. A retry is a new row too (new Execution ID, new lock token): it points back at the attempt it
/// resumes through <see cref="RetryOfExecutionId"/>, shares that attempt's <see cref="RootExecutionId"/> and
/// <see cref="ProcessingDate"/>, and the attempt it resumed is stamped with
/// <see cref="SupersededByExecutionId"/> so it is never resumed twice. An earlier row is never moved back
/// into processing.
///
/// The lock fields mirror LeadDiscoveryLock for this execution and are written only by the lock service,
/// never by the orchestrator - so a heartbeat renewal on another thread cannot be overwritten by the
/// orchestrator saving its counters.
/// </summary>
public class LeadDiscoveryExecution : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid LeadDiscoveryProfileId { get; set; }

    /// <summary>The profile's display name at the time - its TargetBusinessType, since a profile has no
    /// separate name.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Tenant-local calendar date (midnight) the processing belongs to. Inherited by retries, so a
    /// retry on a later day still completes the original day's campaign.</summary>
    public DateTime ProcessingDate { get; set; }

    /// <summary>"Scheduled", "Automatic retry" or "Manual retry".</summary>
    public string Trigger { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public LeadDiscoveryExecutionStatus Status { get; set; } = LeadDiscoveryExecutionStatus.Started;

    // ---- Retry chain ----

    /// <summary>The first execution of this chain; equal to Id for a non-retry execution.</summary>
    public Guid RootExecutionId { get; set; }

    public Guid? RetryOfExecutionId { get; set; }

    public Guid? SupersededByExecutionId { get; set; }

    /// <summary>0 for the original attempt, n for the nth retry.</summary>
    public int RetryCount { get; set; }

    /// <summary>When this execution was last picked up by a retry.</summary>
    public DateTime? LastRetryAtUtc { get; set; }

    /// <summary>The step that failed or was interrupted: Discovery, Customers, Campaign, Templates,
    /// CustomerMapping, CampaignActivation or LockAcquisition.</summary>
    public string? FailedStep { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorDetails { get; set; }

    /// <summary>Human-readable: when and how the remaining work will be retried, or why it will not.</summary>
    public string? NextRetryInfo { get; set; }

    // ---- Distributed lock (written by the lock service only) ----

    public string LockKey { get; set; } = string.Empty;

    public LeadDiscoveryLockStatus LockStatus { get; set; } = LeadDiscoveryLockStatus.Pending;

    /// <summary>A short, non-secret reference to the lock token - enough to tell tokens apart in history.</summary>
    public string? LockTokenReference { get; set; }

    public string? LockOwnerInstanceId { get; set; }

    public DateTime? LockAcquiredAtUtc { get; set; }

    public DateTime? LockExpiresAtUtc { get; set; }

    public DateTime? LockLastRenewedAtUtc { get; set; }

    // ---- Customer processing ----

    public int CustomersDiscovered { get; set; }

    public int CustomersCreated { get; set; }

    public int CustomersDuplicate { get; set; }

    public int CustomersInvalid { get; set; }

    public int CustomersFailed { get; set; }

    /// <summary>Already created by an earlier execution of this chain - not processed again.</summary>
    public int CustomersSkipped { get; set; }

    // ---- Auto-Campaign ----

    public bool AutoCampaignConfigured { get; set; }

    /// <summary>The Auto-Campaign configuration's id. The profile's referred (source) campaign is the whole
    /// Auto-Campaign configuration - its steps are the templates, their order, delays and variables, its
    /// schedule is the sending time - so this is that campaign's id.</summary>
    public Guid? AutoCampaignId { get; set; }

    public Guid? ReferredCampaignId { get; set; }

    public string? ReferredCampaignName { get; set; }

    public Guid? GeneratedCampaignId { get; set; }

    public string? GeneratedCampaignName { get; set; }

    public LeadDiscoveryCampaignStatus CampaignStatus { get; set; } = LeadDiscoveryCampaignStatus.Pending;

    /// <summary>Why the campaign step was skipped, reused or failed - e.g. "Auto-Campaign = Not Configured"
    /// or "No new customers discovered/created. Campaign creation skipped."</summary>
    public string? CampaignNote { get; set; }

    public LeadDiscoveryAssociationStatus TemplateStatus { get; set; } = LeadDiscoveryAssociationStatus.Pending;

    // ---- Campaign customers ----

    public LeadDiscoveryAssociationStatus MappingStatus { get; set; } = LeadDiscoveryAssociationStatus.Pending;

    public int MappingsEligible { get; set; }

    public int MappingsCreated { get; set; }

    public int MappingsExisting { get; set; }

    public int MappingsFailed { get; set; }

    /// <summary>One-line summary, as recorded on the tenant's job schedule.</summary>
    public string? Summary { get; set; }
}
