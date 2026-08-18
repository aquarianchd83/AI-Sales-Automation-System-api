using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Webhooks;

/// <summary>
/// The raw inbound webhook delivery, persisted before any processing happens - if parsing or
/// processing later throws, the actual bytes Meta sent are never lost. Processed asynchronously via a
/// Hangfire job (WebhookEventId only, not the payload itself, is enqueued) so the webhook HTTP request
/// returns fast, which matters: Meta redelivers on a slow or non-2xx response.
/// </summary>
public class WebhookEvent : BaseEntity
{
    public string Provider { get; set; } = "WhatsApp";

    /// <summary>Meta's top-level field name for this change, e.g. "messages".</summary>
    public string EventType { get; set; } = string.Empty;

    public string RawPayload { get; set; } = string.Empty;

    /// <summary>Populated once parsed, for the dedup lookup - null until then, and for payloads
    /// (e.g. a lone status update batch) that do not carry a single obvious message id.</summary>
    public string? WhatsAppMessageId { get; set; }

    public WebhookProcessingStatus ProcessingStatus { get; set; } = WebhookProcessingStatus.Pending;

    public DateTime? ProcessedAt { get; set; }

    public string? ProcessingError { get; set; }

    public DateTime ReceivedAt { get; set; }
}
