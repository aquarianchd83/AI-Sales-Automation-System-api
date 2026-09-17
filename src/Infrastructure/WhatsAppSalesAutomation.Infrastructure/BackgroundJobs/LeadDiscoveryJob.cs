using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Finds new, qualified business leads for one tenant from the web - see LeadDiscoveryRunService for the
/// run and LeadQualification for what "qualified" means. Registered per tenant
/// (<c>lead-discovery:{tenantId}</c>) like every other job in TenantJobCatalog, so each tenant's schedule,
/// last outcome and Run now live on the Platform Admin Console's job list.
///
/// Every tenant gets the job, but it does nothing until the tenant saves and enables a lead discovery
/// profile and has an Anthropic API key configured - those runs are recorded as succeeded with a "Skipped:"
/// summary rather than as failures.
/// </summary>
public class LeadDiscoveryJob
{
    private readonly TenantJobRunner _runner;

    public LeadDiscoveryJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 60)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.LeadDiscovery, async (services, cancellationToken) =>
        {
            var discovery = services.GetRequiredService<ILeadDiscoveryRunService>();
            return await discovery.RunForTenantAsync(tenantId, cancellationToken);
        });
}
