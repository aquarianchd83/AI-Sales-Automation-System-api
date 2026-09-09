using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Shared per-tenant fan-out for every recurring job that has to process every active tenant instead
/// of "the one global tenant" it used to run against - see IActiveTenantLookup's own doc comment for
/// which tenants that is. Not itself named in the SaaS conversion plan's file list, but every one of
/// CampaignInitialSenderJob/FollowUpSchedulerJob/MessageStatusRetryJob/MessageTemplateSyncJob needs the
/// identical shape (list active tenants once, then one fresh DI scope + SetTenant + isolated try/catch
/// per tenant), so this factors that out rather than repeating it four times.
///
/// A fresh <see cref="IServiceScope"/> per tenant, not one scope reused across the loop, is required
/// for correctness, not just tidiness: <c>ApplicationDbContext</c>/<c>ITenantContext</c> are both
/// Scoped, and reusing one scope would accumulate every tenant's tracked entities in the same
/// DbContext's change tracker for the rest of the run - harmless in the narrow case of only ever
/// inserting new rows (TenantStampingSaveChangesInterceptor only stamps EntityState.Added entries,
/// which a previous tenant's already-saved rows no longer are), but a real risk the moment any of
/// these services ever needs to load-then-update an existing row, and pure waste even before that.
///
/// One tenant throwing is caught and logged here, not allowed to abort the loop - a bug or transient
/// failure specific to one tenant's data/credentials must never block every other tenant's campaigns
/// from sending.
/// </summary>
public static class TenantJobRunner
{
    public static async Task RunForEachActiveTenantAsync(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        string jobName,
        Func<IServiceProvider, CancellationToken, Task> runForTenantAsync,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> tenantIds;
        using (var lookupScope = scopeFactory.CreateScope())
        {
            var activeTenantLookup = lookupScope.ServiceProvider.GetRequiredService<IActiveTenantLookup>();
            tenantIds = await activeTenantLookup.GetActiveTenantIdsAsync(cancellationToken);
        }

        foreach (var tenantId in tenantIds)
        {
            using var scope = scopeFactory.CreateScope();

            try
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                await runForTenantAsync(scope.ServiceProvider, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{JobName} failed for tenant {TenantId}", jobName, tenantId);
            }
        }
    }
}
