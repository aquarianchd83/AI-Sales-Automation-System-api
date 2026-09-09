using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Fans out over every active tenant - see CampaignInitialSenderJob's identical doc comment
/// for the TenantJobRunner/scope-per-tenant reasoning.</summary>
public class FollowUpSchedulerJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FollowUpSchedulerJob> _logger;

    public FollowUpSchedulerJob(IServiceScopeFactory scopeFactory, ILogger<FollowUpSchedulerJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    public async Task RunAsync()
    {
        int considered = 0, sent = 0, failed = 0, skipped = 0;

        await TenantJobRunner.RunForEachActiveTenantAsync(_scopeFactory, _logger, nameof(FollowUpSchedulerJob), async (services, cancellationToken) =>
        {
            var sendService = services.GetRequiredService<ICampaignSendService>();
            var result = await sendService.ProcessFollowUpsAsync(cancellationToken: cancellationToken);
            considered += result.Considered;
            sent += result.Sent;
            failed += result.Failed;
            skipped += result.Skipped;
        });

        if (considered > 0)
            _logger.LogInformation(
                "FollowUpSchedulerJob: considered={Considered} sent={Sent} failed={Failed} skipped={Skipped}",
                considered, sent, failed, skipped);
    }
}
