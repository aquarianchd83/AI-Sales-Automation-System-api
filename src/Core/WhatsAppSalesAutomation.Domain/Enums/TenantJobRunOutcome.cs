namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>How one tenant's last background-job run ended, as recorded on
/// <c>TenantJobSchedule.LastRunOutcome</c>. Hangfire's own job state ("Succeeded"/"Failed") is not a
/// substitute: the per-tenant runner catches a tenant's failure deliberately so one tenant cannot
/// abort the run, which means Hangfire sees a succeeded job either way - this is the outcome as the
/// tenant experienced it.</summary>
public enum TenantJobRunOutcome
{
    Succeeded = 0,

    Failed = 1,

    /// <summary>Ran but deliberately did nothing - the tenant stopped being active, or its schedule was
    /// disabled, between the recurring job firing and the run starting. A safety net for the window
    /// where Hangfire's registration hasn't caught up with the database yet, not an error.</summary>
    Skipped = 2
}
