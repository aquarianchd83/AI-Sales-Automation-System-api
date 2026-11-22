namespace WhatsAppSalesAutomation.Application.Razorpay;

/// <summary>The platform's Razorpay account as the test page sees it. Secrets never come back - only whether one is stored and its last four
/// characters.</summary>
public record RazorpaySettingsDto(
    string KeyId,
    string Mode,
    bool HasKeySecret,
    string? KeySecretHint,
    bool HasWebhookSecret,
    string? WebhookSecretHint,
    bool IsConfigured,
    string WebhookPath,
    // Whether tenants billed in rupees pay for a plan through UPI AutoPay. Off until an operator turns it on here.
    bool AutoPayEnabled = false,
    // Switched on AND able to work: keys and the webhook secret are saved. Switched on but not ready means tenants still see the old buttons.
    bool AutoPayReady = false,
    // How payments and orders are READ: "Mcp" (Razorpay's MCP server, default) or "Rest". Creating orders, capture and refunds always use REST.
    string ReadTransport = "Mcp");

/// <param name="KeySecret">null keeps the stored secret, "" clears it, anything else replaces it.</param>
/// <param name="WebhookSecret">Same convention as <paramref name="KeySecret"/>.</param>
public record UpdateRazorpaySettingsRequest(string KeyId, string? KeySecret, string? WebhookSecret);

/// <param name="Enabled">True makes a paid plan, for a tenant billed in rupees, bought through UPI AutoPay.</param>
public record SetRazorpayAutoPayRequest(bool Enabled);

/// <param name="Transport">"Mcp" reads payments and orders through Razorpay's MCP server; "Rest" reads them through the REST API.</param>
public record SetRazorpayTransportRequest(string Transport);

/// <param name="KeySecret">null tests with the stored secret.</param>
public record TestRazorpayKeysRequest(string KeyId, string? KeySecret);

public record RazorpayCheckResultDto(bool Success, string Message);

/// <param name="Amount">In major units (rupees), e.g. 1.00.</param>
public record CreateRazorpayOrderRequest(
    decimal Amount,
    string Currency,
    string? Description,
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerContact);

public record RazorpayPrefillDto(string? Name, string? Email, string? Contact);

/// <summary>Everything Razorpay Checkout needs in the browser. The key id is public by design; the secret never leaves the server.</summary>
public record RazorpayCheckoutDto(
    Guid Id,
    string OrderId,
    string KeyId,
    string Mode,
    long Amount,
    string Currency,
    string Name,
    string Description,
    RazorpayPrefillDto Prefill);

/// <summary>What Checkout's success handler receives, passed on untouched.</summary>
public record VerifyRazorpayPaymentRequest(string RazorpayOrderId, string RazorpayPaymentId, string RazorpaySignature);

/// <summary>What Checkout's payment.failed event reports. Informational: a failed attempt can be followed by a successful one on the same order.</summary>
public record ReportRazorpayFailureRequest(string? PaymentId, string? Code, string? Description, string? Reason);

/// <param name="Amount">Major units; null refunds whatever is left.</param>
public record RefundRazorpayOrderRequest(decimal? Amount);

public record RazorpayStepDto(string Label, bool Ok, string? Detail);

public record RazorpayVerifyResultDto(bool Success, string Message, IReadOnlyList<RazorpayStepDto> Steps, RazorpayOrderDto Order);

public record RazorpayOrderDto(
    Guid Id,
    string OrderId,
    string Receipt,
    long AmountMinor,
    string Currency,
    string Description,
    string Status,
    string Mode,
    string? PaymentId,
    string? Method,
    DateTime? SignatureVerifiedAtUtc,
    DateTime? PaidAtUtc,
    long RefundedMinor,
    string? LastRefundId,
    string? FailureReason,
    string? LastWebhookEvent,
    DateTime? LastWebhookAtUtc,
    DateTime CreatedAt);

public record RazorpayWebhookEventDto(Guid Id, string EventId, string Event, string? OrderId, string? PaymentId, bool Applied, string? Note, DateTime ReceivedAt);

public enum RazorpayWebhookOutcome
{
    /// <summary>Signed, recognised, and changed one of our orders.</summary>
    Applied,

    /// <summary>Signed and stored, but not about any order of ours (or an event we do not act on).</summary>
    Ignored,

    /// <summary>Signed and stored, to be applied by the background job: a subscription event. Storing it is what is acknowledged to
    /// Razorpay - a failure while applying it afterwards loses nothing.</summary>
    Queued,

    /// <summary>The same event id was already received - Razorpay redelivers until it gets a 2xx.</summary>
    Duplicate,

    InvalidSignature,

    /// <summary>No webhook secret is stored, so nothing can be trusted.</summary>
    NotConfigured
}
