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
    /// Persists the raw payload as a new WebhookEvent (stamped to <paramref name="tenantId"/>, already
    /// resolved by the caller off Meta's phone_number_id - see WebhooksController.Receive) and returns
    /// its id - called synchronously from the webhook controller, before any parsing/processing
    /// happens, so the exact bytes Meta sent are never lost even if processing later throws.
    /// </summary>
    Task<Guid> RecordAsync(Guid tenantId, string eventType, string rawPayload, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="tenantId"/> must be the same tenant <see cref="RecordAsync"/> stamped this event
    /// with - InboundWebhookProcessingJob threads it through explicitly as a Hangfire job argument
    /// (see that class's own doc comment for why: this runs in its own Hangfire scope, not the original
    /// request scope, so there is no ambient tenant to fall back on) and calls
    /// <c>ITenantContext.SetTenant(tenantId)</c> with it before touching anything tenant-owned.
    /// <paramref name="attempt"/> is 1 on the first call; InboundWebhookProcessingJob passes an
    /// incremented value when rescheduling a <see cref="WebhookProcessOutcome.RetryNeeded"/> result.
    /// Reprocessing the same event is always safe - status updates only ever advance forward
    /// (CanAdvanceTo) and inbound messages dedup on WhatsAppMessageId, so an earlier attempt's effects
    /// are never re-applied or duplicated.
    /// </summary>
    Task<WebhookProcessOutcome> ProcessAsync(Guid tenantId, Guid webhookEventId, int attempt = 1, CancellationToken cancellationToken = default);
}

public enum WebhookProcessOutcome
{
    /// <summary>Reached a terminal WebhookProcessingStatus (Processed/Duplicate/Failed) - nothing more to do.</summary>
    Completed,

    /// <summary>A status update referenced a message this system does not have yet - most likely our
    /// own outbound send is still mid-flight (WhatsAppMessageId not yet committed) when Meta's webhook
    /// for it arrives. The caller is responsible for scheduling a retry shortly.</summary>
    RetryNeeded
}
