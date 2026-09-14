using Hangfire;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the recurring jobs that are genuinely platform-global, and clears out the global
/// registrations that the per-tenant jobs replaced. Called once from Program.cs after the app is built,
/// immediately before the first <c>ITenantJobProvisioner.ReconcileAllAsync</c> pass, which is what
/// registers the per-tenant jobs themselves. Hangfire persists schedules in SQL Server, so this is
/// idempotent across restarts.
///
/// Every per-tenant job is deliberately absent - campaign sends, follow-ups, retries, template sync and
/// the WhatsApp token refresh are all registered per tenant as <c>{jobType}:{tenantId}</c> from each
/// tenant's own <c>TenantJobSchedules</c> row. See <see cref="TenantJobCatalog"/>.
/// </summary>
public static class RecurringJobsRegistrar
{
    public static void RegisterAll(IRecurringJobManager recurringJobs)
    {
        RemoveLegacyGlobalTenantJobs(recurringJobs);

        // Daily, not hourly: nothing normal depends on this pass - every path that changes a tenant's
        // status or schedule syncs Hangfire inline - so it only exists to catch drift, and drift is rare
        // enough that an operator noticing it and pressing "Reconcile now" is the expected trigger. A
        // scheduled pass this infrequent is the backstop for when nobody is looking, not the mechanism.
        // 00:30 UTC keeps it clear of the per-tenant token refreshes at midnight and the template syncs
        // at :00.
        recurringJobs.AddOrUpdate<TenantJobReconciliationJob>(
            "tenant-job-reconciliation", job => job.RunAsync(), "30 0 * * *");
    }

    /// <summary>
    /// Deletes the global registrations the per-tenant jobs replaced. Necessary, not tidiness: Hangfire
    /// keeps a recurring job until something removes it, so on an existing deployment the old global jobs
    /// would otherwise keep firing alongside the new per-tenant ones - every tenant's campaigns processed
    /// twice a minute by two different jobs, with only the idempotency key standing between that and
    /// duplicate sends, and a leftover global token refresh still calling into code that no longer exists.
    ///
    /// Keyed off the catalog rather than a hard-coded list because the per-tenant ids deliberately reuse
    /// the old global ids as their prefix; <c>RemoveIfExists</c> is a no-op on a fresh database, so this
    /// costs one storage call per job type on every boot forever, which is the cheapest way to be certain
    /// no deployment is left with both.
    /// </summary>
    private static void RemoveLegacyGlobalTenantJobs(IRecurringJobManager recurringJobs)
    {
        foreach (var definition in TenantJobCatalog.All)
            recurringJobs.RemoveIfExists(definition.Key);
    }
}
