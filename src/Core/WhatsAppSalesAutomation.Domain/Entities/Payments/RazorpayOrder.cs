using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Payments;

/// <summary>
/// One Razorpay order made from the platform's Razorpay test page, and what became of it: the browser checkout, the server-side
/// signature check, the capture, webhooks and refunds. Platform-global and deliberately separate from the tenant billing tables
/// (<c>Payment</c>, <c>Subscription</c>) - it proves the gateway end to end without moving anyone's plan or quota.
/// Amounts are in the currency's smallest unit (paise for INR), exactly as Razorpay counts them.
/// </summary>
public class RazorpayOrder : BaseEntity
{
    /// <summary>Razorpay's order id, e.g. "order_Nx1...".</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Our own reference sent with the order (unique, at most 40 characters).</summary>
    public string Receipt { get; set; } = string.Empty;

    public long AmountMinor { get; set; }

    public string Currency { get; set; } = "INR";

    public string Description { get; set; } = string.Empty;

    public RazorpayOrderStatus Status { get; set; } = RazorpayOrderStatus.Created;

    /// <summary>"test" or "live" - which keys made it.</summary>
    public string Mode { get; set; } = "test";

    public string? PaymentId { get; set; }

    /// <summary>card, upi, netbanking, wallet ... as Razorpay reports it.</summary>
    public string? Method { get; set; }

    /// <summary>When the checkout's signature was checked on the server and found genuine.</summary>
    public DateTime? SignatureVerifiedAtUtc { get; set; }

    public DateTime? PaidAtUtc { get; set; }

    public long RefundedMinor { get; set; }

    public string? LastRefundId { get; set; }

    /// <summary>Why it failed, in Razorpay's words, or why we refused it (a bad signature, an amount that does not match).</summary>
    public string? FailureReason { get; set; }

    public string? LastWebhookEvent { get; set; }

    public DateTime? LastWebhookAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

public enum RazorpayOrderStatus
{
    Created = 0,
    Authorized = 1,
    Paid = 2,
    Failed = 3,
    PartiallyRefunded = 4,
    Refunded = 5
}
