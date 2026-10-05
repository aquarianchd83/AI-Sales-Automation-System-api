using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Leads.FollowUps;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Sends the "follow up later" reminders that have fallen due - see LeadFollowUpService for what is checked
/// before each one goes out. Registered per tenant (<c>lead-follow-ups:{tenantId}</c>) like every job in
/// TenantJobCatalog. Runs hourly rather than once a day because the service only sends inside the tenant's
/// daytime hours: an hourly tick lands somewhere in that window whatever the tenant's timezone is.
/// </summary>
public class LeadFollowUpJob
{
    private readonly TenantJobRunner _runner;

    public LeadFollowUpJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 280)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.LeadFollowUps, async (services, cancellationToken) =>
        {
            var followUps = services.GetRequiredService<ILeadFollowUpService>();
            return (await followUps.ProcessDueAsync(cancellationToken)).Describe();
        });
}
