namespace WhatsAppSalesAutomation.Domain.Enums;

public enum WebhookProcessingStatus
{
    Pending = 0,
    Processed = 1,
    Failed = 2,

    /// <summary>Meta redelivers webhooks it did not get a fast enough 200 for - this is the outcome
    /// when the same WhatsAppMessageId/EventType is seen again, not an error.</summary>
    Duplicate = 3,

    /// <summary>A status update referenced a message this system has not recorded yet - almost always
    /// a race between our own outbound send committing its WhatsAppMessageId and Meta's webhook for it
    /// arriving first. InboundWebhookProcessingJob reprocesses the same event shortly after (safe:
    /// every effect in InboundWebhookProcessor is idempotent), so this is a transient state, not a
    /// final outcome - unless retries are exhausted, at which point the event is reclassified as
    /// Processed or Failed instead. See IInboundWebhookProcessor.ProcessAsync.</summary>
    PendingRetry = 4
}
