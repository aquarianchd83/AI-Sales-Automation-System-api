using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Payments;

/// <summary>A webhook Razorpay delivered and whose signature checked out. <see cref="EventId"/> (Razorpay's x-razorpay-event-id) is unique, so a
/// redelivery of the same event is recognised and not applied twice. Only signed events are stored: an anonymous caller must not be able to
/// fill this table.</summary>
public class RazorpayWebhookEvent : BaseEntity
{
    public string EventId { get; set; } = string.Empty;

    /// <summary>e.g. payment.captured, payment.failed, order.paid, refund.processed.</summary>
    public string Event { get; set; } = string.Empty;

    public string? OrderId { get; set; }

    public string? PaymentId { get; set; }

    /// <summary>Razorpay's subscription id (sub_...) when the event is about one.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Whether it matched one of our orders and changed it.</summary>
    public bool Applied { get; set; }

    public string? Note { get; set; }

    /// <summary>The body as received.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Where the event is on its way from "stored" to "applied". An event is stored first and applied afterwards, so a failure
    /// while applying it loses nothing: the row is still here to try again.</summary>
    public RazorpayWebhookProcessingStatus ProcessingStatus { get; set; } = RazorpayWebhookProcessingStatus.Received;

    /// <summary>When it was applied - or, while it is Failed, when it was last tried: the retry pass waits from this.</summary>
    public DateTime? ProcessedAtUtc { get; set; }

    /// <summary>How many times applying it has failed.</summary>
    public int RetryCount { get; set; }

    /// <summary>The last failure, for support.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Razorpay's own created_at for the event - when it happened there, not when it reached us. Events arrive in any order.</summary>
    public DateTime? EventCreatedAtUtc { get; set; }
}

public enum RazorpayWebhookProcessingStatus
{
    /// <summary>Signed and stored; not yet applied.</summary>
    Received = 0,

    Completed = 2,

    /// <summary>Applying it failed; <see cref="RazorpayWebhookEvent.ErrorMessage"/> says why. Retried until the attempts run out.</summary>
    Failed = 3,

    /// <summary>Genuine, but not about anything of ours, or an event we do not act on.</summary>
    Ignored = 4
}
