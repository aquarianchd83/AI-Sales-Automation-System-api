namespace WhatsAppSalesAutomation.Application.Webhooks;

/// <summary>
/// Turns one persisted <c>WebhookEvent</c> into domain effects: message status updates, new inbound
/// messages, customer auto-creation, opt-out interception, and handoff creation. Takes the event's id
/// rather than its content so it can be called from a Hangfire job (durable, retryable) with a small,
/// serializable argument instead of the raw payload.
/// </summary>
public interface IInboundWebhookProcessor
{
    /// <summary>
    /// Persists the raw payload as a new WebhookEvent and returns its id - called synchronously from
    /// the webhook controller, before any parsing/processing happens, so the exact bytes Meta sent
    /// are never lost even if processing later throws.
    /// </summary>
    Task<Guid> RecordAsync(string eventType, string rawPayload, CancellationToken cancellationToken = default);

    Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default);
}
