using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Runs for one tenant per execution - see CampaignInitialSenderJob's identical doc comment for
/// the per-tenant registration/TenantJobRunner reasoning.</summary>
public class FollowUpSchedulerJob
{
    private readonly TenantJobRunner _runner;

    public FollowUpSchedulerJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 280)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.CampaignFollowUps, async (services, cancellationToken) =>
        {
            return await CampaignJobNotices.RunAsync(services, tenantId, "follow-ups", includeDueScheduled: false,
                async (sendService, campaignId) =>
                    CampaignJobNotices.Describe(await sendService.ProcessFollowUpsAsync(campaignId, cancellationToken)),
                cancellationToken);
        });
}
