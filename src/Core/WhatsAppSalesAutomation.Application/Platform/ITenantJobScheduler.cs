namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>What Hangfire's recurring job registry looks like for one job, read back out of storage.
/// <see cref="LastJobState"/> is Hangfire's own view ("Succeeded"/"Failed"/"Processing") of the last
/// triggered run, which is not the same thing as how the tenant's work went - the per-tenant runner
/// swallows a tenant's exception on purpose, so a tenant-level failure still shows here as Succeeded.
/// <c>TenantJobSchedule.LastRunOutcome</c> is the tenant-level answer; both are surfaced.</summary>
public record RecurringJobRegistrationState(
    string RecurringJobId,
    string? Cron,
    DateTime? NextExecutionUtc,
    DateTime? LastExecutionUtc,
    string? LastJobState,
    string? Error);

/// <summary>
/// The Application layer's view of Hangfire's recurring job registry - the one place a
/// PlatformSuperAdmin's schedule edit turns into an actual scheduler change. Abstracted (rather than
/// referencing Hangfire from <c>PlatformJobService</c>/<c>TenantJobProvisioner</c> directly) for the
/// usual Clean Architecture reason, plus one concrete one: the key-to-CLR-job-type mapping every
/// AddOrUpdate needs lives in Infrastructure alongside the job classes themselves, so it cannot be
/// expressed in Application code at all.
///
/// Every method is synchronous and non-awaiting because Hangfire's own client API is - these are
/// storage writes against Hangfire's tables, deliberately not routed through <c>IApplicationDbContext</c>.
/// </summary>
public interface ITenantJobScheduler
{
    /// <summary>Whether Hangfire/Cronos would accept this expression. Checked before persisting a
    /// schedule, so an invalid cron is a 400 on the operator's request rather than a job that silently
    /// stops firing.</summary>
    bool IsValidCron(string cronExpression);

    /// <summary>Registers (or re-points) <c>{jobType}:{tenantId}</c>. Idempotent - Hangfire treats a
    /// repeat of the same id/cron as a no-op, which is what makes the boot-time reconcile safe.</summary>
    void AddOrUpdateTenantJob(Guid tenantId, string jobType, string cronExpression);

    /// <summary>Removes one tenant's copy of one job. Safe when it was never registered.</summary>
    void RemoveTenantJob(Guid tenantId, string jobType);

    /// <summary>Fires one tenant's copy of a job immediately, without disturbing its schedule - the
    /// console's "Run now". Returns the enqueued background job id so the caller can put it in the audit
    /// trail and the operator can find that exact run in the Hangfire dashboard.</summary>
    string TriggerTenantJob(Guid tenantId, string jobType);

    /// <summary>Registry state for the given recurring job ids, keyed by id; ids that are not registered
    /// are simply absent from the result rather than mapping to null.</summary>
    IReadOnlyDictionary<string, RecurringJobRegistrationState> GetRegistrations(IEnumerable<string> recurringJobIds);

    /// <summary>Every currently registered recurring job id whose prefix is a known per-tenant job type.
    /// Used by the reconcile pass to find orphans - a tenant deleted while the app was down leaves its
    /// registrations behind, and nothing else would ever remove them.</summary>
    IReadOnlyList<string> GetRegisteredTenantJobIds();

    /// <summary>The recurring jobs that are not per-tenant at all (currently just the reconcile pass
    /// itself), so the console can show them as platform-scoped instead of leaving an operator to wonder
    /// why they have no tenant.</summary>
    IReadOnlyList<RecurringJobRegistrationState> GetPlatformJobs();
}
