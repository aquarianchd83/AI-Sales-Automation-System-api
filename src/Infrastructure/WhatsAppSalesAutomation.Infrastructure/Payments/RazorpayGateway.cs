using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Infrastructure.Payments;

/// <summary>
/// Razorpay's REST API over HttpClient (https://api.razorpay.com/v1), authenticated per call with HTTP basic auth (key id : key secret) - set on
/// each request, never on the shared client, so two key pairs can never cross. Razorpay's errors come back as
/// <c>{"error":{"code":"BAD_REQUEST_ERROR","description":"..."}}</c>; the description is what the caller shows.
/// </summary>
public class RazorpayGateway : IRazorpayGateway
{
    public const string DefaultBaseUrl = "https://api.razorpay.com/v1/";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly ILogger<RazorpayGateway> _logger;

    public RazorpayGateway(HttpClient http, ILogger<RazorpayGateway> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress ??= new Uri(DefaultBaseUrl);
        if (_http.Timeout > TimeSpan.FromSeconds(30))
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<RazorpayResult<bool>> PingAsync(RazorpayCredentials credentials, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<JsonElement>(credentials, HttpMethod.Get, "orders?count=1", null, cancellationToken);
        return result.Success ? RazorpayResult<bool>.Ok(true) : RazorpayResult<bool>.Fail(result.Error!);
    }

    public async Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
        RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<OrderBody>(credentials, HttpMethod.Post, "orders", new { amount = amountMinor, currency, receipt, notes }, cancellationToken);
        return result.Success && result.Value?.Id is { } id
            ? RazorpayResult<RazorpayOrderInfo>.Ok(new RazorpayOrderInfo(id, result.Value.Amount, result.Value.Currency ?? currency, result.Value.Receipt ?? receipt, result.Value.Status ?? "created"))
            : RazorpayResult<RazorpayOrderInfo>.Fail(result.Error ?? "Razorpay returned no order id.");
    }

    public async Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default) =>
        ToPayment(await SendAsync<PaymentBody>(credentials, HttpMethod.Get, $"payments/{Uri.EscapeDataString(paymentId)}", null, cancellationToken));

    public async Task<RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>> FetchOrderPaymentsAsync(
        RazorpayCredentials credentials, string orderId, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaymentCollectionBody>(credentials, HttpMethod.Get, $"orders/{Uri.EscapeDataString(orderId)}/payments", null, cancellationToken);
        if (!result.Success || result.Value is null)
            return RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>.Fail(result.Error ?? "Razorpay returned no payments.");

        var payments = (result.Value.Items ?? new List<PaymentBody>()).Where(p => p.Id is not null)
            .Select(p => new RazorpayPaymentInfo(p.Id!, p.OrderId ?? orderId, p.Amount, p.Currency ?? "INR", p.Status ?? "unknown", p.Method, p.AmountRefunded, p.ErrorDescription))
            .ToList();
        return RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>.Ok(payments);
    }

    public async Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
        RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default) =>
        ToPayment(await SendAsync<PaymentBody>(credentials, HttpMethod.Post, $"payments/{Uri.EscapeDataString(paymentId)}/capture", new { amount = amountMinor, currency }, cancellationToken));

    public async Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(
        RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default)
    {
        object body = amountMinor is { } a ? new { amount = a } : new { };
        var result = await SendAsync<RefundBody>(credentials, HttpMethod.Post, $"payments/{Uri.EscapeDataString(paymentId)}/refund", body, cancellationToken);
        return result.Success && result.Value?.Id is { } id
            ? RazorpayResult<RazorpayRefundInfo>.Ok(new RazorpayRefundInfo(id, result.Value.PaymentId ?? paymentId, result.Value.Amount, result.Value.Status ?? "pending"))
            : RazorpayResult<RazorpayRefundInfo>.Fail(result.Error ?? "Razorpay returned no refund id.");
    }

    public async Task<RazorpayResult<RazorpayPlanInfo>> CreatePlanAsync(
        RazorpayCredentials credentials, string period, int interval, string name, long amountMinor, string currency,
        IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default)
    {
        var body = new { period, interval, item = new { name, amount = amountMinor, currency }, notes };
        var result = await SendAsync<PlanBody>(credentials, HttpMethod.Post, "plans", body, cancellationToken);
        return result.Success && result.Value?.Id is { } id
            ? RazorpayResult<RazorpayPlanInfo>.Ok(new RazorpayPlanInfo(
                id, result.Value.Period ?? period, result.Value.Interval ?? interval, result.Value.Item?.Amount ?? amountMinor, result.Value.Item?.Currency ?? currency))
            : RazorpayResult<RazorpayPlanInfo>.Fail(result.Error ?? "Razorpay returned no plan id.");
    }

    public async Task<RazorpayResult<RazorpaySubscriptionInfo>> CreateSubscriptionAsync(
        RazorpayCredentials credentials, string planId, int totalCount, bool customerNotify, long? expireByUnix,
        IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default)
    {
        // expire_by is left out when null (the serializer drops nulls), so Razorpay applies its own default.
        var body = new { plan_id = planId, total_count = totalCount, quantity = 1, customer_notify = customerNotify, expire_by = expireByUnix, notes };
        return ToSubscription(await SendAsync<SubscriptionBody>(credentials, HttpMethod.Post, "subscriptions", body, cancellationToken));
    }

    public async Task<RazorpayResult<RazorpaySubscriptionInfo>> FetchSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default) =>
        ToSubscription(await SendAsync<SubscriptionBody>(credentials, HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}", null, cancellationToken));

    public async Task<RazorpayResult<RazorpaySubscriptionInfo>> CancelSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, bool cancelAtCycleEnd, CancellationToken cancellationToken = default) =>
        ToSubscription(await SendAsync<SubscriptionBody>(
            credentials, HttpMethod.Post, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}/cancel", new { cancel_at_cycle_end = cancelAtCycleEnd }, cancellationToken));

    public async Task<RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>> FetchSubscriptionInvoicesAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default)
    {
        // 100 is Razorpay's largest page: more than eight years of monthly invoices.
        var result = await SendAsync<InvoiceCollectionBody>(
            credentials, HttpMethod.Get, $"invoices?subscription_id={Uri.EscapeDataString(subscriptionId)}&count=100", null, cancellationToken);
        if (!result.Success || result.Value is null)
            return RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>.Fail(result.Error ?? "Razorpay returned no invoices.");

        var invoices = (result.Value.Items ?? new List<InvoiceBody>()).Where(i => i.Id is not null).Select(ToInvoice).ToList();
        return RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>.Ok(invoices);
    }

    public async Task<RazorpayResult<RazorpayInvoiceInfo>> FetchInvoiceAsync(
        RazorpayCredentials credentials, string invoiceId, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<InvoiceBody>(credentials, HttpMethod.Get, $"invoices/{Uri.EscapeDataString(invoiceId)}", null, cancellationToken);
        return result.Success && result.Value?.Id is not null
            ? RazorpayResult<RazorpayInvoiceInfo>.Ok(ToInvoice(result.Value))
            : RazorpayResult<RazorpayInvoiceInfo>.Fail(result.Error ?? "Razorpay returned no invoice.");
    }

    private static RazorpayInvoiceInfo ToInvoice(InvoiceBody i) => new(
        i.Id!, i.SubscriptionId, i.PaymentId, i.OrderId, i.Status ?? "unknown", i.Amount ?? 0, i.AmountPaid ?? 0, i.Currency ?? "INR",
        i.PaidAt, i.IssuedAt, i.BillingStart, i.BillingEnd, i.ShortUrl, i.InvoiceNumber);

    private static RazorpayResult<RazorpaySubscriptionInfo> ToSubscription(RazorpayResult<SubscriptionBody> result) =>
        result.Success && result.Value?.Id is { } id
            ? RazorpayResult<RazorpaySubscriptionInfo>.Ok(new RazorpaySubscriptionInfo(
                id, result.Value.PlanId, result.Value.Status ?? "unknown", result.Value.ShortUrl,
                result.Value.TotalCount ?? 0, result.Value.PaidCount ?? 0, result.Value.RemainingCount ?? 0,
                result.Value.CurrentStart, result.Value.CurrentEnd, result.Value.ChargeAt, result.Value.StartAt, result.Value.EndAt, result.Value.EndedAt,
                result.Value.CustomerId, result.Value.PaymentMethod))
            : RazorpayResult<RazorpaySubscriptionInfo>.Fail(result.Error ?? "Razorpay returned no subscription.");

    private static RazorpayResult<RazorpayPaymentInfo> ToPayment(RazorpayResult<PaymentBody> result) =>
        result.Success && result.Value?.Id is { } id
            ? RazorpayResult<RazorpayPaymentInfo>.Ok(new RazorpayPaymentInfo(
                id, result.Value.OrderId, result.Value.Amount, result.Value.Currency ?? "INR", result.Value.Status ?? "unknown",
                result.Value.Method, result.Value.AmountRefunded, result.Value.ErrorDescription))
            : RazorpayResult<RazorpayPaymentInfo>.Fail(result.Error ?? "Razorpay returned no payment.");

    private async Task<RazorpayResult<T>> SendAsync<T>(RazorpayCredentials credentials, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.KeyId}:{credentials.KeySecret}")));
            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);

            using var response = await _http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorOf(text) ?? $"Razorpay returned {(int)response.StatusCode}.";
                _logger.LogWarning("Razorpay {Method} {Path} failed ({Status}): {Error}", method, path, (int)response.StatusCode, error);
                return RazorpayResult<T>.Fail(error);
            }

            var value = JsonSerializer.Deserialize<T>(text, Json);
            return value is null ? RazorpayResult<T>.Fail("Razorpay returned an empty response.") : RazorpayResult<T>.Ok(value);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            _logger.LogWarning(ex, "Razorpay {Method} {Path} failed", method, path);
            return RazorpayResult<T>.Fail($"Could not reach Razorpay: {ex.Message}");
        }
    }

    private static string? ErrorOf(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            // Razorpay normally answers { "error": { "description": "..." } }, but a proxy, an auth failure or a gateway error can
            // answer with a bare string or a differently shaped body. Read what is there rather than throw and hide the real reason.
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return root.GetString();
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var e))
                return null;
            if (e.ValueKind == JsonValueKind.String)
                return e.GetString();
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class OrderBody
    {
        public string? Id { get; set; }
        public long Amount { get; set; }
        public string? Currency { get; set; }
        public string? Receipt { get; set; }
        public string? Status { get; set; }
    }

    private sealed class PaymentBody
    {
        public string? Id { get; set; }
        public string? OrderId { get; set; }
        public long Amount { get; set; }
        public string? Currency { get; set; }
        public string? Status { get; set; }
        public string? Method { get; set; }
        public long AmountRefunded { get; set; }
        public string? ErrorDescription { get; set; }
    }

    private sealed class PaymentCollectionBody
    {
        public List<PaymentBody>? Items { get; set; }
    }

    private sealed class PlanBody
    {
        public string? Id { get; set; }
        public string? Period { get; set; }
        public int? Interval { get; set; }
        public PlanItemBody? Item { get; set; }
    }

    private sealed class PlanItemBody
    {
        public long? Amount { get; set; }
        public string? Currency { get; set; }
    }

    /// <summary>Counts and times are nullable: Razorpay sends null for the ones that do not apply yet (no cycle before the mandate is authorised).</summary>
    private sealed class SubscriptionBody
    {
        public string? Id { get; set; }
        public string? PlanId { get; set; }
        public string? Status { get; set; }
        public string? ShortUrl { get; set; }
        public int? TotalCount { get; set; }
        public int? PaidCount { get; set; }
        public int? RemainingCount { get; set; }
        public long? CurrentStart { get; set; }
        public long? CurrentEnd { get; set; }
        public long? ChargeAt { get; set; }
        public long? StartAt { get; set; }
        public long? EndAt { get; set; }
        public long? EndedAt { get; set; }
        public string? CustomerId { get; set; }
        public string? PaymentMethod { get; set; }
    }

    private sealed class InvoiceCollectionBody
    {
        public List<InvoiceBody>? Items { get; set; }
    }

    private sealed class InvoiceBody
    {
        public string? Id { get; set; }
        public string? SubscriptionId { get; set; }
        public string? PaymentId { get; set; }
        public string? OrderId { get; set; }
        public string? Status { get; set; }
        public long? Amount { get; set; }
        public long? AmountPaid { get; set; }
        public string? Currency { get; set; }
        public long? PaidAt { get; set; }
        public long? IssuedAt { get; set; }
        public long? BillingStart { get; set; }
        public long? BillingEnd { get; set; }
        public string? ShortUrl { get; set; }
        public string? InvoiceNumber { get; set; }
    }

    private sealed class RefundBody
    {
        public string? Id { get; set; }
        public string? PaymentId { get; set; }
        public long Amount { get; set; }
        public string? Status { get; set; }
    }
}
