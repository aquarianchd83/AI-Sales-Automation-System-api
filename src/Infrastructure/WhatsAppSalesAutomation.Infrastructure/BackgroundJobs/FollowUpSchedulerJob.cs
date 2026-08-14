using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

public class FollowUpSchedulerJob
{
    private readonly ICampaignSendService _sendService;
    private readonly ILogger<FollowUpSchedulerJob> _logger;

    public FollowUpSchedulerJob(ICampaignSendService sendService, ILogger<FollowUpSchedulerJob> logger)
    {
        _sendService = sendService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    public async Task RunAsync()
    {
        var result = await _sendService.ProcessFollowUpsAsync();
        if (result.Considered > 0)
            _logger.LogInformation(
                "FollowUpSchedulerJob: considered={Considered} sent={Sent} failed={Failed} skipped={Skipped}",
                result.Considered, result.Sent, result.Failed, result.Skipped);
    }
}
