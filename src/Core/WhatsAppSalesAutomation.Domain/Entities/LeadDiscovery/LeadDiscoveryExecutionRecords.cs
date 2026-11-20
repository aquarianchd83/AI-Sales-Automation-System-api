using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

/// <summary>
/// One discovered customer's result within one LeadDiscoveryExecution. Written as Pending (with
/// <see cref="CandidateJson"/>) before the customer's own transaction runs, and flipped to Created inside that
/// same transaction - so a row reads Created exactly when its Customer was committed. A Pending or Failed row
/// keeps its candidate so a retry can process it without researching it again.
/// </summary>
public class LeadDiscoveryExecutionCustomer : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ExecutionId { get; set; }

    /// <summary>The row in the previous execution this one resumes, for a retry.</summary>
    public Guid? RetriedFromId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? DiscoveredLeadId { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public LeadDiscoveryCustomerStatus Status { get; set; } = LeadDiscoveryCustomerStatus.Pending;

    /// <summary>True for a Skipped row that failed validation, as opposed to one skipped because an earlier
    /// execution already created it.</summary>
    public bool IsInvalid { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorDetails { get; set; }

    /// <summary>The qualified candidate, serialized, while it may still need processing. Cleared once
    /// the row is Created or Duplicate.</summary>
    public string? CandidateJson { get; set; }

    /// <summary>What the discovery agent reported about this candidate (LeadDiscoveryCandidateSnapshot, as JSON), kept whatever became of it -
    /// unlike <see cref="CandidateJson"/>, which is only the work still to do. Null on a row from before candidates were kept.</summary>
    public string? CandidateDetailsJson { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    /// <summary>Whether the customer's number passed the DND / outreach-eligibility check. NotChecked for a row with no customer.</summary>
    public LeadDiscoveryVerificationStatus VerificationStatus { get; set; } = LeadDiscoveryVerificationStatus.NotChecked;

    /// <summary>The provider's reason code for a Fail or Error, for example "DND".</summary>
    public string? VerificationReasonCode { get; set; }

    public string? VerificationReason { get; set; }

    public DateTime? VerifiedAtUtc { get; set; }

    /// <summary>The provider's reference for the batch this customer was submitted in.</summary>
    public string? VerificationRequestId { get; set; }
}

/// <summary>
/// A lead the discovery agent found and that qualified, but that the batch had no room for. The tenant already paid for the research that found it,
/// so it is kept and used first by the profile's next run instead of being researched (and paid for) again. Not charged until it becomes a customer.
/// </summary>
public class LeadDiscoveryCachedLead : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid LeadDiscoveryProfileId { get; set; }

    /// <summary>The execution that found it.</summary>
    public Guid SourceExecutionId { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public string? PhoneKey { get; set; }

    public string NameKey { get; set; } = string.Empty;

    /// <summary>The qualified lead (VerifiedLead, as JSON).</summary>
    public string CandidateJson { get; set; } = string.Empty;

    /// <summary>What the agent reported about it (LeadDiscoveryCandidateSnapshot, as JSON).</summary>
    public string? CandidateDetailsJson { get; set; }

    /// <summary>After this the lead is stale and is no longer offered to a run.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Set when a run took it, or dropped it (it became a customer some other way, or no longer fits the profile).</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>The execution that took it; null when it was dropped instead.</summary>
    public Guid? UsedExecutionId { get; set; }
}

/// <summary>One Auto-Campaign template (a step of the referred campaign) and whether it was associated with
/// the generated campaign in this execution.</summary>
public class LeadDiscoveryExecutionTemplate : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ExecutionId { get; set; }

    public Guid CampaignId { get; set; }

    /// <summary>The referred campaign's step the association was copied from.</summary>
    public Guid SourceStepId { get; set; }

    public Guid? TemplateId { get; set; }

    public string TemplateName { get; set; } = string.Empty;

    /// <summary>0 = Initial, n = nth follow-up - the template's position in the sequence.</summary>
    public int Sequence { get; set; }

    public int DelayDaysAfterPrevious { get; set; }

    /// <summary>The generated campaign's step, once associated.</summary>
    public Guid? CampaignStepId { get; set; }

    public LeadDiscoveryAssociationStatus Status { get; set; } = LeadDiscoveryAssociationStatus.Pending;

    public string? ErrorMessage { get; set; }
}

/// <summary>One lock-state transition of one execution - the lock's audit trail.</summary>
public class LeadDiscoveryLockTransition : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ExecutionId { get; set; }

    public Guid LeadDiscoveryProfileId { get; set; }

    public DateTime ProcessingDate { get; set; }

    public LeadDiscoveryLockStatus FromStatus { get; set; }

    public LeadDiscoveryLockStatus ToStatus { get; set; }

    public DateTime TransitionAtUtc { get; set; }

    public string? LockTokenReference { get; set; }

    public string? OwnerInstanceId { get; set; }

    public string? Reason { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// The campaign generated for one logical Auto-Campaign run. The unique index on (TenantId,
/// LeadDiscoveryProfileId, AutoCampaignId, ProcessingDate) is the final guarantee that a retry, a second run
/// the same day, or two racing instances can never create a second campaign for the same key - whoever
/// loses the race reads the winner's row and reuses its campaign.
/// </summary>
public class LeadDiscoveryGeneratedCampaign : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid LeadDiscoveryProfileId { get; set; }

    public Guid AutoCampaignId { get; set; }

    public DateTime ProcessingDate { get; set; }

    public Guid CampaignId { get; set; }

    /// <summary>The execution that created it.</summary>
    public Guid ExecutionId { get; set; }
}

/// <summary>
/// The shared lease behind the lead-discovery distributed lock, one row per lock key
/// ("lead-discovery:{TenantId}:{ProfileId}"). Deliberately not ITenantOwned: it is read and written only by the
/// lock service, in its own short-lived scopes with no tenant context, always matching on the full key plus
/// token - never through a tenant query filter.
///
/// Ownership is a compare-and-set on this row: acquire succeeds only when the row is free or its lease has
/// expired, renew and release only when <see cref="LockToken"/>, <see cref="ExecutionId"/> and
/// <see cref="OwnerInstanceId"/> all still match and the lease has not expired. An older execution therefore
/// cannot renew or release a lock a newer execution has since taken.
/// </summary>
public class LeadDiscoveryLock
{
    public string LockKey { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public Guid LeadDiscoveryProfileId { get; set; }

    public Guid ExecutionId { get; set; }

    public Guid LockToken { get; set; }

    public string OwnerInstanceId { get; set; } = string.Empty;

    public DateTime AcquiredAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? LastRenewedAtUtc { get; set; }

    /// <summary>Acquired while held, Released once let go. An Acquired row whose lease has passed is free.</summary>
    public LeadDiscoveryLockStatus Status { get; set; }
}
