using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Payments;

/// <summary>
/// One attempt by a tenant to top up its credit wallet through Razorpay - the tenant-scoped counterpart of the platform's test-page
/// <see cref="RazorpayOrder"/>. The tenant and the amount are fixed on the server when the order is made; the browser can change neither.
/// The wallet is credited at most once per row: <see cref="Status"/> only moves forward, <see cref="PaymentId"/> is unique, and the ledger
/// entry is keyed by this row's id.
/// Money is decimal rupees (never floating point); Razorpay's own paise are <see cref="AmountMinor"/>.
/// </summary>
public class PaymentTransaction : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Our reference, also sent to Razorpay as the order's receipt (unique, at most 40 characters).</summary>
    public string InternalTransactionId { get; set; } = string.Empty;

    /// <summary>Razorpay's order id, e.g. "order_Nx1...".</summary>
    public string RazorpayOrderId { get; set; } = string.Empty;

    /// <summary>The payment that settled it (pay_...). Unique when set: one gateway payment can only ever be applied to one top-up.</summary>
    public string? RazorpayPaymentId { get; set; }

    /// <summary>What the tenant asked to add, before tax - the amount that becomes credits.</summary>
    public decimal Amount { get; set; }

    /// <summary>Tax added on top, snapshotted when the order was made.</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>What the tenant pays Razorpay: <see cref="Amount"/> plus <see cref="TaxAmount"/>.</summary>
    public decimal TotalAmount { get; set; }

    /// <summary><see cref="TotalAmount"/> in paise, exactly as sent to Razorpay.</summary>
    public long AmountMinor { get; set; }

    public string Currency { get; set; } = "INR";

    /// <summary>Wallet credits this top-up adds: <see cref="Amount"/> times the conversion rate in force when the order was made.</summary>
    public decimal Credits { get; set; }

    /// <summary>The conversion rate (credits per rupee) used, kept so a later rate change never rewrites history.</summary>
    public decimal CreditsPerRupee { get; set; }

    public string? TaxLinesJson { get; set; }

    /// <summary>Snapshots the matching <c>Payment</c> row is built from.</summary>
    public int AmountUsdCents { get; set; }

    public decimal FxRateToInr { get; set; }

    public decimal TotalInr { get; set; }

    public string? CountryCode { get; set; }

    public string? StateCode { get; set; }

    public PaymentTransactionStatus Status { get; set; } = PaymentTransactionStatus.Created;

    /// <summary>upi, card, netbanking, wallet ... as Razorpay reports it.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>"test" or "live" - which keys made the order.</summary>
    public string Mode { get; set; } = "test";

    public string? FailureReason { get; set; }

    /// <summary>The <c>Payment</c> row written when the wallet was credited.</summary>
    public Guid? PaymentId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>When Razorpay captured the money (as the server learned it).</summary>
    public DateTime? CapturedAt { get; set; }

    /// <summary>When the wallet was credited.</summary>
    public DateTime? CreditedAt { get; set; }

    /// <summary>When the last reconciliation pass looked at it, so the job does not re-ask Razorpay about the same row every run.</summary>
    public DateTime? LastReconciledAt { get; set; }

    public long RefundedMinor { get; set; }

    /// <summary>Set when this is the purchase of a credit pack (the "Buy" buttons) rather than a free-amount top-up: the price is the pack's, the
    /// credits are the pack's units, and the <c>Payment</c> row is a credit-pack payment.</summary>
    public Guid? CreditPackId { get; set; }

    /// <summary>Optimistic concurrency: two deliveries settling or refunding the same top-up at once (browser and webhook, or refund.created and
    /// refund.processed arriving together) cannot both write - the loser's save fails and it re-reads the row.</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public enum PaymentTransactionStatus
{
    /// <summary>The order exists; no payment seen yet.</summary>
    Created = 0,

    /// <summary>Razorpay authorized a payment that is not yet captured. Nothing is credited.</summary>
    Authorized = 1,

    /// <summary>Money captured and the wallet credited. The only state that has credited anything.</summary>
    Credited = 2,

    /// <summary>The payment failed, or did not match the order. Nothing was credited; a later successful attempt on the same order still can.</summary>
    Failed = 3,

    /// <summary>No payment arrived within the window: the checkout was abandoned.</summary>
    Expired = 4,

    /// <summary>Credited, and later refunded in part; the refunded share was clawed back.</summary>
    PartiallyRefunded = 5,

    Refunded = 6
}
