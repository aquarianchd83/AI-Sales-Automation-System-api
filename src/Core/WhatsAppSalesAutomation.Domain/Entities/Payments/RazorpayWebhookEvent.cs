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

    /// <summary>Whether it matched one of our orders and changed it.</summary>
    public bool Applied { get; set; }

    public string? Note { get; set; }

    /// <summary>The body as received, capped - enough to see what Razorpay said.</summary>
    public string Payload { get; set; } = string.Empty;
}
