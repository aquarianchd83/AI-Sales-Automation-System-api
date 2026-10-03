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
}
