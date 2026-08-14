using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Thin Hangfire-triggered wrapper - all the actual logic lives in <see cref="ICampaignSendService"/>
/// so it stays framework-agnostic and directly unit-testable. DisableConcurrentExecution is the third
/// line of defense against a double send, behind the idempotency key and its unique DB index (see
/// CampaignSendService's remarks).
/// </summary>
public class CampaignInitialSenderJob
{
    private readonly ICampaignSendService _sendService;
    private readonly ILogger<CampaignInitialSenderJob> _logger;

    public CampaignInitialSenderJob(ICampaignSendService sendService, ILogger<CampaignInitialSenderJob> logger)
    {
        _sendService = sendService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    public async Task RunAsync()
    {
        var result = await _sendService.ProcessInitialSendsAsync();
        if (result.Considered > 0)
            _logger.LogInformation(
                "CampaignInitialSenderJob: considered={Considered} sent={Sent} failed={Failed} skipped={Skipped}",
                result.Considered, result.Sent, result.Failed, result.Skipped);
    }
}
