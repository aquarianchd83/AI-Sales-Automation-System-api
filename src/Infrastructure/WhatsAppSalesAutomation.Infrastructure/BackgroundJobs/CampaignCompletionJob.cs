using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Runs for one tenant per execution - see CampaignInitialSenderJob's identical doc comment for
/// the per-tenant registration/TenantJobRunner reasoning. Scheduled twice a day (see TenantJobCatalog):
/// closing a finished campaign is not time-critical, so it does not ride on the frequent send jobs.
/// Checks each running campaign in turn so the tenant is told about each one by name (CampaignJobNotices).</summary>
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
            await CampaignJobNotices.RunAsync(services, tenantId, "completion check", includeDueScheduled: false,
                async (sendService, campaignId) =>
                    (await sendService.CompleteFinishedCampaignsAsync(campaignId, cancellationToken)).Completed.Count > 0
                        ? "The campaign has finished and was closed."
                        : "The campaign is still running.",
                cancellationToken));
}
