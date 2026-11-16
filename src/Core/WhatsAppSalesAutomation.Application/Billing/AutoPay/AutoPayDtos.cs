namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>Body of POST billing/autopay/checkout. Only the plan: what it costs is worked out on the server, never taken from the browser.</summary>
public record AutoPayCheckoutRequest(Guid PlanId);

/// <summary>Body of POST billing/autopay/subscriptions/{id}/cancel.</summary>
/// <param name="CancelAtCycleEnd">True stops renewal after the period already paid for; false cancels the mandate now. Either way, access
/// that has been paid for is kept until the period ends.</param>
public record CancelAutoPayRequest(bool CancelAtCycleEnd = true);

/// <summary>Whether this tenant pays for a plan through AutoPay, and if not, why.</summary>
/// <param name="Mode">"test" or "live" - which Razorpay keys are in use. Null when Razorpay is not set up.</param>
public record AutoPayAvailabilityDto(bool Available, string? Reason, string? Mode);

/// <summary>
/// What the browser needs to send the tenant to Razorpay to authorise the mandate. <paramref name="AuthorizationUrl"/> is Razorpay's hosted
/// page; <paramref name="KeyId"/> and <paramref name="SubscriptionId"/> open Razorpay Checkout instead, for a screen that prefers the
/// pop-up. The key id is public by design; the secret never leaves the server. Coming back from either proves nothing: the mandate counts
/// only once Razorpay confirms it.
/// </summary>
/// <param name="Status">Always "PendingAuthorization" from a checkout.</param>
/// <param name="AmountMinor">The recurring debit in paise, tax included.</param>
public record AutoPayCheckoutDto(
    Guid Id,
    string SubscriptionId,
    string Status,
    string AuthorizationUrl,
    string KeyId,
    string Mode,
    long AmountMinor,
    string Currency,
    string PlanName);

/// <summary>One AutoPay mandate as the tenant sees it.</summary>
/// <param name="Status">What to show: one of <see cref="AutoPayDisplayStatus"/>.</param>
/// <param name="GatewayStatus">Razorpay's own state, for support.</param>
/// <param name="Amount">The recurring debit in rupees, tax included.</param>
/// <param name="NextRenewalUtc">When Razorpay next debits. Null when it will not renew.</param>
/// <param name="AutoRenew">Whether another debit is coming.</param>
/// <param name="AuthorizationUrl">Where to authorise it - set only while it waits for that.</param>
public record AutoPaySubscriptionDto(
    Guid Id,
    string SubscriptionId,
    Guid PlanId,
    string PlanName,
    string Status,
    string GatewayStatus,
    decimal Amount,
    string CurrencyCode,
    string BillingFrequency,
    DateTime? CurrentPeriodStartUtc,
    DateTime? CurrentPeriodEndUtc,
    DateTime? NextRenewalUtc,
    bool AutoRenew,
    bool CancelAtCycleEnd,
    DateTime? CancelledAtUtc,
    string? PaymentMethod,
    string? AuthorizationUrl,
    string Mode,
    DateTime CreatedAt);

/// <summary>One invoice Razorpay raised for a billing cycle of the mandate.</summary>
/// <param name="Status">Razorpay's own: draft, issued, partially_paid, paid, expired, cancelled.</param>
/// <param name="Amount">In rupees.</param>
/// <param name="TransactionId">The Razorpay payment that settled it, once paid.</param>
/// <param name="Url">Razorpay's hosted page for the invoice.</param>
public record AutoPayInvoiceDto(
    string InvoiceId,
    string? InvoiceNumber,
    string Status,
    decimal Amount,
    decimal AmountPaid,
    string CurrencyCode,
    DateTime? IssuedAtUtc,
    DateTime? PaidAtUtc,
    DateTime? PeriodStartUtc,
    DateTime? PeriodEndUtc,
    string? TransactionId,
    string? Url);

/// <summary>The states the billing screen shows. Derived from Razorpay's state - never from anything the browser reported.</summary>
public static class AutoPayDisplayStatus
{
    public const string PendingAuthorization = "PendingAuthorization";

    /// <summary>The mandate is authorised; Razorpay has not yet confirmed the first debit.</summary>
    public const string PaymentPending = "PaymentPending";

    public const string Active = "Active";

    /// <summary>A debit failed and Razorpay is retrying it.</summary>
    public const string PastDue = "PastDue";

    /// <summary>Every retry failed; nothing more is debited until the tenant acts.</summary>
    public const string Halted = "Halted";

    public const string Paused = "Paused";

    /// <summary>Still running, but it will not renew.</summary>
    public const string CancellationScheduled = "CancellationScheduled";

    public const string Cancelled = "Cancelled";

    public const string Expired = "Expired";
}
