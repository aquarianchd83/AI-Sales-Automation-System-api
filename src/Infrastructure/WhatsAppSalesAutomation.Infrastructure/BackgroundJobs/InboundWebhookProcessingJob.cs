using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Webhooks;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Enqueued once per received webhook (see WebhooksController), not on a recurring schedule like the
/// campaign jobs - so unlike them, this deliberately has no [DisableConcurrentExecution]: that
/// attribute would serialize processing of every webhook delivery globally, when different
/// WebhookEventIds are entirely independent and safe to process in parallel. Per-event idempotency
/// already comes from Message.WhatsAppMessageId's own dedup inside InboundWebhookProcessor.
/// </summary>
public class InboundWebhookProcessingJob
{
    private readonly IInboundWebhookProcessor _processor;
    private readonly ILogger<InboundWebhookProcessingJob> _logger;

    public InboundWebhookProcessingJob(IInboundWebhookProcessor processor, ILogger<InboundWebhookProcessingJob> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    public async Task RunAsync(Guid webhookEventId)
    {
        _logger.LogInformation("Processing WebhookEvent {Id}", webhookEventId);
        await _processor.ProcessAsync(webhookEventId);
    }
}
