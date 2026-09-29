using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Runs for one tenant per execution - see CampaignInitialSenderJob's identical doc comment for
/// the per-tenant registration/TenantJobRunner reasoning. Scheduled twice a day (see TenantJobCatalog):
/// closing a finished campaign is not time-critical, so it does not ride on the frequent send jobs.</summary>
public class CampaignCompletionJob
{
    private readonly TenantJobRunner _runner;

    public CampaignCompletionJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 280)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.CampaignCompletion, async (services, cancellationToken) =>
        {
            var sendService = services.GetRequiredService<ICampaignSendService>();
            var completed = await sendService.CompleteFinishedCampaignsAsync(cancellationToken);

            return $"completed={completed}";
        });
}
