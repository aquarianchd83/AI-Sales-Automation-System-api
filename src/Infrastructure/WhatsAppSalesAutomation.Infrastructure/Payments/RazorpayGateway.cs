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
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("description", out var d) ? d.GetString() : null;
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

    private sealed class RefundBody
    {
        public string? Id { get; set; }
        public string? PaymentId { get; set; }
        public long Amount { get; set; }
        public string? Status { get; set; }
    }
}
