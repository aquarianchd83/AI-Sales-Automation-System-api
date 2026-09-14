namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>What one <see cref="ITenantJobProvisioner.ReconcileAllAsync"/> pass changed. Returned rather
/// than only logged so the reconcile recurring job can log a single line, and so the Platform Admin
/// Console's "reconcile now" action can tell the operator what it actually did.</summary>
public record TenantJobReconcileSummary(
    int TenantsExamined,
    int SchedulesCreated,
    int JobsRegistered,
    int JobsRemoved,
    int OrphanRegistrationsRemoved);

/// <summary>
/// Keeps Hangfire's recurring job registry in step with the <c>TenantJobSchedules</c> table, which is
/// the source of truth. Two entry points, because there are two ways registrations go stale:
///
/// <list type="bullet">
/// <item><see cref="SyncTenantAsync"/> - called inline by everything that creates a tenant or changes
/// its status (self-serve signup, operator-initiated creation, suspend/reactivate/delete) and by the
/// console after a schedule edit, so the change takes effect immediately rather than at the next
/// reconcile.</item>
/// <item><see cref="ReconcileAllAsync"/> - the safety net, run at startup and on a schedule. Catches
/// everything an inline call cannot: a status change made somewhere that forgot to call it (billing
/// activating a subscription, a future admin path), a tenant created or deleted while the process was
/// down, and registrations orphaned by either.</item>
/// </list>
///
/// Both are idempotent, so calling one after the other (or twice) changes nothing the second time.
/// </summary>
public interface ITenantJobProvisioner
{
    /// <summary>Ensures this tenant has a schedule row per <see cref="TenantJobCatalog"/> entry, then
    /// registers or removes each of its recurring jobs to match that row and the tenant's current status.
    /// Safe for a tenant id that does not exist (or no longer should run anything) - it removes rather
    /// than throws, which is what makes it safe to call from a delete path.</summary>
    Task SyncTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<TenantJobReconcileSummary> ReconcileAllAsync(CancellationToken cancellationToken = default);
}
