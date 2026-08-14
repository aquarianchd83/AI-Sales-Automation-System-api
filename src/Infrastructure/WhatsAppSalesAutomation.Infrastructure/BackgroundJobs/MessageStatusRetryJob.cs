using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

public class MessageStatusRetryJob
{
    private readonly ICampaignSendService _sendService;
    private readonly ILogger<MessageStatusRetryJob> _logger;

    public MessageStatusRetryJob(ICampaignSendService sendService, ILogger<MessageStatusRetryJob> logger)
    {
        _sendService = sendService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    public async Task RunAsync()
    {
        var result = await _sendService.RetryFailedSendsAsync();
        if (result.Considered > 0)
            _logger.LogInformation(
                "MessageStatusRetryJob: considered={Considered} sent={Sent} failed={Failed} skipped={Skipped}",
                result.Considered, result.Sent, result.Failed, result.Skipped);
    }
}
