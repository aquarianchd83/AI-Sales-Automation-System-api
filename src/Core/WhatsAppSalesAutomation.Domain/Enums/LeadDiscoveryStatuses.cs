namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Overall status of one LeadDiscoveryExecution.</summary>
public enum LeadDiscoveryExecutionStatus
{
    /// <summary>Row written, lock not yet acquired.</summary>
    Started = 0,

    /// <summary>A new (non-retry) execution holding the lock and doing work.</summary>
    Processing = 1,

    /// <summary>Every step succeeded, or was legitimately skipped.</summary>
    Completed = 2,

    /// <summary>Something succeeded and something failed, and no automatic retry is left.</summary>
    PartiallyCompleted = 3,

    /// <summary>Nothing useful was done - blocked by another execution, or failed before any work.</summary>
    Failed = 4,

    /// <summary>Unfinished work remains and a retry will pick it up.</summary>
    RetryPending = 5,

    /// <summary>A retry execution holding the lock and resuming its predecessor's unfinished work.</summary>
    Retrying = 6
}

/// <summary>What happened to one discovered customer within one execution.</summary>
public enum LeadDiscoveryCustomerStatus
{
    /// <summary>Qualified and not a known duplicate, waiting for its own transaction. A Pending row left
    /// behind by an execution that stopped (lock lost, crash) is what a retry resumes.</summary>
    Pending = 0,

    Processing = 1,

    /// <summary>Inserted as a new Customer in this execution.</summary>
    Created = 2,

    /// <summary>Matched an existing customer or discovered lead - nothing inserted.</summary>
    Duplicate = 3,

    /// <summary>Not processed: invalid (failed qualification / no usable phone) or already created by an
    /// earlier execution in the same retry chain.</summary>
    Skipped = 4,

    /// <summary>Its transaction rolled back. Retryable.</summary>
    Failed = 5
}

/// <summary>Status of the generated-campaign step of an execution.</summary>
public enum LeadDiscoveryCampaignStatus
{
    Pending = 0,
    Creating = 1,

    /// <summary>Created by this execution, or an existing one reused for the same logical key.</summary>
    Created = 2,

    /// <summary>Auto-Campaign not configured, or no new customers.</summary>
    Skipped = 3,

    Failed = 4,
    RetryPending = 5
}

/// <summary>Status of the template-association and campaign-customer mapping steps.</summary>
public enum LeadDiscoveryAssociationStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    RetryPending = 4,

    /// <summary>Not applicable - the campaign step was skipped.</summary>
    Skipped = 5
}

/// <summary>
/// The distributed-lock lifecycle of one execution. Only <see cref="Acquired"/> permits new business
/// processing; <see cref="Blocked"/>, <see cref="Released"/>, <see cref="Expired"/> and <see cref="Lost"/>
/// are terminal. Legal transitions are defined in one place - LeadDiscoveryLockStateMachine.
/// </summary>
public enum LeadDiscoveryLockStatus
{
    Pending = 0,
    Acquiring = 1,
    Acquired = 2,
    Renewing = 3,
    RenewalFailed = 4,
    ReleasePending = 5,
    Released = 6,
    Blocked = 7,
    Expired = 8,
    Lost = 9
}
