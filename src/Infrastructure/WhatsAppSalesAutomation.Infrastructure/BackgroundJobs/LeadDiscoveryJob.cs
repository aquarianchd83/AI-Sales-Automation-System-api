using Hangfire;
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
///
/// [DisableConcurrentExecutionPerTenant] is only this process's first line of defence; the tenant+profile
/// distributed lock inside LeadDiscoveryRunService is what keeps two instances (or a scheduled run and a
/// manual retry) from processing the same profile at once.
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

    /// <summary>A manual retry from Lead Discovery History. Recorded on the same schedule row as the scheduled
    /// run, so the console's last-run summary shows it too.</summary>
    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public Task RetryAsync(Guid tenantId, Guid executionId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.LeadDiscovery, async (services, cancellationToken) =>
        {
            var discovery = services.GetRequiredService<ILeadDiscoveryRunService>();
            return "manual retry: " + await discovery.RetryExecutionAsync(tenantId, executionId, cancellationToken);
        });
}

/// <summary>Queues <see cref="LeadDiscoveryJob.RetryAsync"/> on Hangfire.</summary>
public class HangfireLeadDiscoveryRetryScheduler : ILeadDiscoveryRetryScheduler
{
    private readonly IBackgroundJobClient _backgroundJobs;

    public HangfireLeadDiscoveryRetryScheduler(IBackgroundJobClient backgroundJobs)
    {
        _backgroundJobs = backgroundJobs;
    }

    public string EnqueueRetry(Guid tenantId, Guid executionId) =>
        _backgroundJobs.Enqueue<LeadDiscoveryJob>(job => job.RetryAsync(tenantId, executionId));
}
