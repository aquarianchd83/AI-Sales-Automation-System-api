using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// One tenant's schedule for one recurring background job - the database-side counterpart of the
/// Hangfire recurring job registered as <c>{JobType}:{TenantId}</c>. Every active tenant gets one row
/// per <c>TenantJobCatalog</c> entry, created by <c>ITenantJobProvisioner</c> when the tenant is
/// created (and backfilled for pre-existing tenants by the AddTenantJobSchedules migration).
///
/// Platform-owned, deliberately NOT <see cref="ITenantOwned"/>, for the same reason as
/// <see cref="PlatformAuditLogEntry"/>: only a PlatformSuperAdmin may read or change it, and both the
/// Platform Admin Console (cross-tenant, no ambient tenant) and the job provisioner (running at
/// startup, likewise no ambient tenant) have to query it across every tenant at once - a tenant query
/// filter would hide every row from both. <see cref="TenantId"/> is therefore a plain foreign key, not
/// a filtered tenancy discriminator; the <c>TenantStampingSaveChangesInterceptor</c> leaves it alone
/// and callers set it explicitly.
///
/// This table is the source of truth for the schedule, not Hangfire's own <c>Hangfire.Set</c>/
/// <c>Hangfire.Hash</c> rows: on every boot (and on a reconcile pass) the registrations are rebuilt
/// from these rows, so an operator's edit survives a restart and a drifted/orphaned Hangfire
/// registration is corrected rather than trusted.
/// </summary>
public class TenantJobSchedule : BaseEntity
{
    public Guid TenantId { get; set; }

    /// <summary>One of <c>TenantJobTypes</c>' constants - a free-text snapshot rather than an enum,
    /// same reasoning as <see cref="PlatformAuditLogEntry.Action"/>: the set of per-tenant jobs will
    /// grow, and a row for a job type a later release renames or retires must stay readable (and
    /// deletable) instead of becoming unrepresentable.</summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>Hangfire/Cronos 5-field cron expression, defaulted from the catalog and editable per
    /// tenant by a PlatformSuperAdmin - validated through <c>ITenantJobScheduler.IsValidCron</c> before
    /// it is ever stored, so a bad expression can't reach Hangfire's own AddOrUpdate and leave the job
    /// permanently unschedulable.</summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>False pauses just this one job for just this one tenant: the recurring job is removed
    /// from Hangfire entirely rather than left registered, so nothing sits in the queue waiting - the
    /// row here is what remembers the cron to restore when it is re-enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime? LastRunAtUtc { get; set; }

    public TenantJobRunOutcome? LastRunOutcome { get; set; }

    /// <summary>Human-readable one-liner from the run - the job's own counters on success
    /// ("considered=3 sent=2 failed=0 skipped=1"), the exception message on failure, the reason on a
    /// skip. Surfaced verbatim on the console's job row so an operator doesn't have to go digging in
    /// the Hangfire dashboard or the log file for the common case.</summary>
    public string? LastRunSummary { get; set; }

    public int? LastRunDurationMs { get; set; }

    /// <summary>Reset to zero by any successful run. Drives the console's "failing" filter - a single
    /// transient failure is noise, a climbing count is a tenant that needs attention (typically its own
    /// WhatsApp/AI credentials).</summary>
    public int ConsecutiveFailureCount { get; set; }
}
