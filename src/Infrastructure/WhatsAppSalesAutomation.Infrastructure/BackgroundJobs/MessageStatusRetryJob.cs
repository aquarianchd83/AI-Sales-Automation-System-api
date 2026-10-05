using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Runs for one tenant per execution - see CampaignInitialSenderJob's identical doc comment for
/// the per-tenant registration/TenantJobRunner reasoning.</summary>
public class MessageStatusRetryJob
{
    private readonly TenantJobRunner _runner;

    public MessageStatusRetryJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 280)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.CampaignSendRetries, async (services, cancellationToken) =>
        {
            var summary = await CampaignJobNotices.RunAsync(services, tenantId, "send retries", includeDueScheduled: false,
                async (sendService, campaignId) =>
                    CampaignJobNotices.Describe(await sendService.RetryFailedSendsAsync(campaignId, cancellationToken)),
                cancellationToken);

            // Messages that belong to no campaign (a reply, a one-off send) fail too and are retried here as well; they have
            // no campaign to name, so they are retried without a notice of their own.
            var others = await services.GetRequiredService<ICampaignSendService>().RetryFailedSendsAsync(cancellationToken: cancellationToken);
            return $"{summary} Other messages: {CampaignJobNotices.Describe(others)}";
        });
}
