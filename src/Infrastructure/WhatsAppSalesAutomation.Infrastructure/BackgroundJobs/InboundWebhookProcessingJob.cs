using Hangfire;
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
    /// <summary>Backoff before retrying a WebhookProcessOutcome.RetryNeeded result - short at first
    /// since this is normally just our own send finishing its SaveChangesAsync a beat after Meta's
    /// webhook arrives, growing for the rare case it takes longer. One entry per attempt beyond the
    /// first; the last entry repeats for any further attempt. How many attempts are made at all is
    /// IInboundWebhookProcessor's own call (MaxAttempts) - this job just keeps rescheduling for as
    /// long as it's told to.</summary>
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)
    };

    private readonly IInboundWebhookProcessor _processor;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<InboundWebhookProcessingJob> _logger;

    public InboundWebhookProcessingJob(
        IInboundWebhookProcessor processor,
        IBackgroundJobClient backgroundJobClient,
        ILogger<InboundWebhookProcessingJob> logger)
    {
        _processor = processor;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public Task RunAsync(Guid tenantId, Guid webhookEventId) => RunAsync(tenantId, webhookEventId, 1);

    /// <summary>
    /// <paramref name="tenantId"/> must survive the Hangfire enqueue boundary as an explicit argument
    /// rather than relying on ambient context - this job runs in its own DI scope with no request to
    /// inherit a tenant from, unlike a controller action. WebhooksController.Receive resolves it once
    /// (off Meta's phone_number_id) and passes it straight through to the initial Enqueue call.
    /// </summary>
    public async Task RunAsync(Guid tenantId, Guid webhookEventId, int attempt)
    {
        _logger.LogInformation("Processing WebhookEvent {Id} for tenant {TenantId} (attempt {Attempt})", webhookEventId, tenantId, attempt);
        var outcome = await _processor.ProcessAsync(tenantId, webhookEventId, attempt);

        if (outcome != WebhookProcessOutcome.RetryNeeded)
            return;

        var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
        _backgroundJobClient.Schedule<InboundWebhookProcessingJob>(job => job.RunAsync(tenantId, webhookEventId, attempt + 1), delay);
    }
}
