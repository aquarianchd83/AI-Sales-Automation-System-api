namespace WhatsAppSalesAutomation.Application.Razorpay;

/// <summary>The key pair every Razorpay API call is authenticated with (HTTP basic auth: key id, key secret).</summary>
public record RazorpayCredentials(string KeyId, string KeySecret)
{
    public string Mode => RazorpayKeys.ModeOf(KeyId);
}

/// <summary>The outcome of one Razorpay API call. Never thrown: a declined key, a network failure or Razorpay's own error is a normal result
/// the caller shows, in Razorpay's words where it gave any.</summary>
public record RazorpayResult<T>(bool Success, T? Value, string? Error)
{
    public static RazorpayResult<T> Ok(T value) => new(true, value, null);

    public static RazorpayResult<T> Fail(string error) => new(false, default, error);
}

public record RazorpayOrderInfo(string Id, long Amount, string Currency, string Receipt, string Status);

/// <param name="Status">created, authorized, captured, refunded or failed.</param>
public record RazorpayPaymentInfo(
    string Id,
    string? OrderId,
    long Amount,
    string Currency,
    string Status,
    string? Method,
    long AmountRefunded,
    string? ErrorDescription);

public record RazorpayRefundInfo(string Id, string PaymentId, long Amount, string Status);

/// <param name="Period">daily, weekly, monthly, quarterly or yearly.</param>
/// <param name="Interval">How many periods make one billing cycle.</param>
public record RazorpayPlanInfo(string Id, string Period, int Interval, long Amount, string Currency);

/// <summary>A subscription as Razorpay reports it. Every time is Unix seconds, as Razorpay sends it; null where Razorpay sent none.</summary>
/// <param name="Status">created, authenticated, active, pending, halted, cancelled, completed, expired or paused.</param>
/// <param name="ShortUrl">Razorpay's hosted page for the authorisation payment.</param>
/// <param name="CurrentStart">Start of the billing cycle Razorpay counts as current.</param>
/// <param name="CurrentEnd">End of that cycle - what the customer has paid through to.</param>
/// <param name="ChargeAt">When Razorpay next tries to debit.</param>
/// <param name="EndedAt">When it was cancelled or completed.</param>
public record RazorpaySubscriptionInfo(
    string Id,
    string? PlanId,
    string Status,
    string? ShortUrl,
    int TotalCount,
    int PaidCount,
    int RemainingCount,
    long? CurrentStart,
    long? CurrentEnd,
    long? ChargeAt,
    long? StartAt,
    long? EndAt,
    long? EndedAt,
    string? CustomerId,
    string? PaymentMethod);

/// <summary>Razorpay's REST API (https://api.razorpay.com/v1). Implemented in Infrastructure over HttpClient.</summary>
public interface IRazorpayGateway
{
    /// <summary>Proves the keys work without changing anything: lists at most one order.</summary>
    Task<RazorpayResult<bool>> PingAsync(RazorpayCredentials credentials, CancellationToken cancellationToken = default);

    Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
        RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default);

    Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default);

    /// <summary>Captures an authorized payment. Needed only when the account is not set to capture automatically.</summary>
    Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
        RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default);

    /// <param name="amountMinor">null refunds what is left of the payment.</param>
    Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(
        RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default);

    // ── Subscriptions (recurring billing, UPI AutoPay) ─────────────────────────────────────

    /// <summary>Creates a plan: a fixed amount debited every <paramref name="interval"/> <paramref name="period"/>s. A plan can never be changed or
    /// deleted afterwards, so the caller keeps its id and reuses it.</summary>
    Task<RazorpayResult<RazorpayPlanInfo>> CreatePlanAsync(
        RazorpayCredentials credentials, string period, int interval, string name, long amountMinor, string currency,
        IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default);

    /// <summary>Creates a subscription to a plan. Nothing is debited until the customer authorises it on <see cref="RazorpaySubscriptionInfo.ShortUrl"/>.</summary>
    /// <param name="totalCount">How many billing cycles it runs for.</param>
    /// <param name="expireByUnix">Until when the customer may authorise it; null leaves it to Razorpay.</param>
    Task<RazorpayResult<RazorpaySubscriptionInfo>> CreateSubscriptionAsync(
        RazorpayCredentials credentials, string planId, int totalCount, bool customerNotify, long? expireByUnix,
        IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default);

    Task<RazorpayResult<RazorpaySubscriptionInfo>> FetchSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a subscription - for good: a cancelled subscription can never be restarted.</summary>
    /// <param name="cancelAtCycleEnd">True stops it after the cycle already paid for; false stops it now.</param>
    Task<RazorpayResult<RazorpaySubscriptionInfo>> CancelSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, bool cancelAtCycleEnd, CancellationToken cancellationToken = default);

    /// <summary>The invoices Razorpay raised for a subscription - one per billing cycle, each naming the payment that settled it.</summary>
    Task<RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>> FetchSubscriptionInvoicesAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default);

    Task<RazorpayResult<RazorpayInvoiceInfo>> FetchInvoiceAsync(
        RazorpayCredentials credentials, string invoiceId, CancellationToken cancellationToken = default);
}

/// <summary>An invoice as Razorpay reports it. Amounts in paise, times in Unix seconds.</summary>
/// <param name="Status">draft, issued, partially_paid, paid, expired, cancelled or deleted.</param>
/// <param name="BillingStart">Start of the billing cycle the invoice is for.</param>
/// <param name="ShortUrl">Razorpay's hosted page for the invoice.</param>
public record RazorpayInvoiceInfo(
    string Id,
    string? SubscriptionId,
    string? PaymentId,
    string? OrderId,
    string Status,
    long Amount,
    long AmountPaid,
    string Currency,
    long? PaidAt,
    long? IssuedAt,
    long? BillingStart,
    long? BillingEnd,
    string? ShortUrl,
    string? InvoiceNumber);
