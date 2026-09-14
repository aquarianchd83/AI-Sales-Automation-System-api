using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Thin Hangfire-triggered wrapper - all the actual logic lives in <see cref="ICampaignSendService"/>
/// so it stays framework-agnostic and directly unit-testable.
///
/// One registered recurring job per tenant (<c>campaign-initial-sends:{tenantId}</c>, created by
/// <c>ITenantJobProvisioner</c>), so <paramref name="tenantId"/> arrives as an explicit argument rather
/// than this listing tenants itself the way the pre-per-tenant version did. <see cref="TenantJobRunner"/>
/// turns that id into a tenant-scoped DI scope before <see cref="ICampaignSendService"/> is resolved, so
/// the existing tenant query filters do the rest without the send service needing to know tenancy exists.
/// </summary>
public class CampaignInitialSenderJob
{
    private readonly TenantJobRunner _runner;

    public CampaignInitialSenderJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 280)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.CampaignInitialSends, async (services, cancellationToken) =>
        {
            var sendService = services.GetRequiredService<ICampaignSendService>();
            var result = await sendService.ProcessInitialSendsAsync(cancellationToken: cancellationToken);

            return $"considered={result.Considered} sent={result.Sent} failed={result.Failed} skipped={result.Skipped}";
        });
}
