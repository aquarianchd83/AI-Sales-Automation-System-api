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
/// The four campaign/template jobs are deliberately absent: they are now registered per tenant as
/// <c>{jobType}:{tenantId}</c> from each tenant's own <c>TenantJobSchedules</c> row - see
/// <see cref="TenantJobCatalog"/>.
/// </summary>
public static class RecurringJobsRegistrar
{
    public static void RegisterAll(IRecurringJobManager recurringJobs)
    {
        RemoveLegacyGlobalTenantJobs(recurringJobs);

        recurringJobs.AddOrUpdate<WhatsAppTokenRefreshJob>(
            "whatsapp-token-refresh", job => job.RunAsync(), Cron.Daily());

        // Hourly, and offset to :30 so a boot-time reconcile and this one are never trying to rebuild the
        // same registrations at the same moment as the hourly template syncs they are registering.
        recurringJobs.AddOrUpdate<TenantJobReconciliationJob>(
            "tenant-job-reconciliation", job => job.RunAsync(), "30 * * * *");
    }

    /// <summary>
    /// Deletes the four pre-per-tenant registrations. Necessary, not tidiness: Hangfire keeps a recurring
    /// job until something removes it, so on an existing deployment the old global fan-out jobs would
    /// otherwise keep firing alongside the new per-tenant ones - every tenant's campaigns processed twice
    /// a minute by two different jobs, with only the idempotency key standing between that and duplicate
    /// sends.
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
