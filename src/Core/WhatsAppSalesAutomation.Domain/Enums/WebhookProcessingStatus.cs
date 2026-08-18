namespace WhatsAppSalesAutomation.Domain.Enums;

public enum WebhookProcessingStatus
{
    Pending = 0,
    Processed = 1,
    Failed = 2,

    /// <summary>Meta redelivers webhooks it did not get a fast enough 200 for - this is the outcome
    /// when the same WhatsAppMessageId/EventType is seen again, not an error.</summary>
    Duplicate = 3
}
