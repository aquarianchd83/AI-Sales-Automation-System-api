using System.Text.Json;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Infrastructure.Payments;

/// <summary>
/// The backend's Razorpay gateway: reads through Razorpay's MCP server (<see cref="RazorpayMcpClient"/>), writes through the REST API
/// (<see cref="RazorpayGateway"/>).
/// <para>
/// Razorpay's hosted MCP server is read-only: it has <c>fetch_payment</c>, <c>fetch_order_payments</c> and <c>fetch_all_orders</c> (the key check),
/// but no <c>create_order</c>, <c>capture_payment</c> or <c>create_refund</c> - calling them fails with "tool not found". So creating an order,
/// capturing and refunding always use REST, and a read whose tool the configured server does not have falls back to REST as well. Plans,
/// subscriptions and invoices (UPI AutoPay) have no MCP tools and use REST too.
/// </para>
/// <para>
/// <c>Razorpay:Transport</c> = <c>Rest</c> (Platform Admin Console &gt; Razorpay) sends everything straight to the REST API again - a switch for an
/// MCP outage, no redeploy. <c>Razorpay:McpUrl</c> points at another MCP server (e.g. a self-hosted one).
/// </para>
/// Signatures (checkout and webhook) are never Razorpay calls: they are checked in the backend with the key secret and webhook secret, whichever transport is used.
/// </summary>
public class RazorpayMcpGateway : IRazorpayGateway
{
    public const string TransportKey = "Razorpay:Transport";
    public const string McpUrlKey = "Razorpay:McpUrl";
    public const string TransportMcp = "Mcp";
    public const string TransportRest = "Rest";

    private readonly RazorpayMcpClient _mcp;
    private readonly RazorpayGateway _rest;
    private readonly IAppSettingsStore _settings;
    private (string Transport, string Url)? _route;

    public RazorpayMcpGateway(RazorpayMcpClient mcp, RazorpayGateway rest, IAppSettingsStore settings)
    {
        _mcp = mcp;
        _rest = rest;
        _settings = settings;
    }

    public async Task<RazorpayResult<bool>> PingAsync(RazorpayCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.PingAsync(credentials, cancellationToken);

        var result = await CallAsync(credentials, "fetch_all_orders", new Dictionary<string, object?> { ["count"] = 1 }, cancellationToken);
        if (IsMissingTool(result, "fetch_all_orders"))
            return await _rest.PingAsync(credentials, cancellationToken);
        return result.Success ? RazorpayResult<bool>.Ok(true) : RazorpayResult<bool>.Fail(result.Error!);
    }

    public Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
        RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default)
    {
        // A write: Razorpay's MCP server has no create_order tool.
        return _rest.CreateOrderAsync(credentials, amountMinor, currency, receipt, notes, cancellationToken);
    }

    public async Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.FetchPaymentAsync(credentials, paymentId, cancellationToken);

        var result = await CallAsync(credentials, "fetch_payment", new Dictionary<string, object?> { ["payment_id"] = paymentId }, cancellationToken);
        return IsMissingTool(result, "fetch_payment") ? await _rest.FetchPaymentAsync(credentials, paymentId, cancellationToken) : ToPayment(result, null);
    }

    public async Task<RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>> FetchOrderPaymentsAsync(
        RazorpayCredentials credentials, string orderId, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.FetchOrderPaymentsAsync(credentials, orderId, cancellationToken);

        var result = await CallAsync(credentials, "fetch_order_payments", new Dictionary<string, object?> { ["order_id"] = orderId }, cancellationToken);
        if (IsMissingTool(result, "fetch_order_payments"))
            return await _rest.FetchOrderPaymentsAsync(credentials, orderId, cancellationToken);
        if (!result.Success)
            return RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>.Fail(result.Error!);

        // Razorpay's collection: { "entity": "collection", "count": n, "items": [ ... ] }.
        var items = result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : result.Value.ValueKind == JsonValueKind.Array ? result.Value.EnumerateArray() : default;

        var payments = new List<RazorpayPaymentInfo>();
        foreach (var item in items)
        {
            if (Payment(item, orderId) is { } p)
                payments.Add(p);
        }
        return RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>.Ok(payments);
    }

    // Writes: Razorpay's MCP server has no capture_payment or create_refund tool.
    public Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
        RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default) =>
        _rest.CapturePaymentAsync(credentials, paymentId, amountMinor, currency, cancellationToken);

    public Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default) =>
        _rest.RefundAsync(credentials, paymentId, amountMinor, cancellationToken);

    public Task<RazorpayResult<RazorpayPlanInfo>> CreatePlanAsync(
        RazorpayCredentials credentials, string period, int interval, string name, long amountMinor, string currency, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default) =>
        _rest.CreatePlanAsync(credentials, period, interval, name, amountMinor, currency, notes, cancellationToken);

    public Task<RazorpayResult<RazorpaySubscriptionInfo>> CreateSubscriptionAsync(
        RazorpayCredentials credentials, string planId, int totalCount, bool customerNotify, long? expireByUnix, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default) =>
        _rest.CreateSubscriptionAsync(credentials, planId, totalCount, customerNotify, expireByUnix, notes, cancellationToken);

    public Task<RazorpayResult<RazorpaySubscriptionInfo>> FetchSubscriptionAsync(RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default) =>
        _rest.FetchSubscriptionAsync(credentials, subscriptionId, cancellationToken);

    public Task<RazorpayResult<RazorpaySubscriptionInfo>> CancelSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, bool cancelAtCycleEnd, CancellationToken cancellationToken = default) =>
        _rest.CancelSubscriptionAsync(credentials, subscriptionId, cancelAtCycleEnd, cancellationToken);

    public Task<RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>> FetchSubscriptionInvoicesAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default) =>
        _rest.FetchSubscriptionInvoicesAsync(credentials, subscriptionId, cancellationToken);

    public Task<RazorpayResult<RazorpayInvoiceInfo>> FetchInvoiceAsync(RazorpayCredentials credentials, string invoiceId, CancellationToken cancellationToken = default) =>
        _rest.FetchInvoiceAsync(credentials, invoiceId, cancellationToken);

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private async Task<(string Transport, string Url)> RouteAsync(CancellationToken cancellationToken)
    {
        if (_route is { } known)
            return known;

        var stored = await _settings.GetAllAsync(cancellationToken);
        var transport = stored.TryGetValue(TransportKey, out var t) && string.Equals(t?.Trim(), TransportRest, StringComparison.OrdinalIgnoreCase) ? TransportRest : TransportMcp;
        var url = stored.TryGetValue(McpUrlKey, out var u) && Uri.TryCreate(u?.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.ToString()
            : RazorpayMcpClient.DefaultUrl;
        _route = (transport, url);
        return _route.Value;
    }

    private async Task<bool> RestAsync(CancellationToken cancellationToken) => (await RouteAsync(cancellationToken)).Transport == TransportRest;

    private async Task<RazorpayResult<JsonElement>> CallAsync(
        RazorpayCredentials credentials, string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
        await _mcp.CallToolAsync((await RouteAsync(cancellationToken)).Url, credentials, tool, arguments, cancellationToken);

    /// <summary>The MCP server does not have this tool (Razorpay's reply: "tool 'x' not found: tool not found"), so the REST API answers instead.</summary>
    private static bool IsMissingTool(RazorpayResult<JsonElement> result, string tool) =>
        !result.Success && result.Error is { } error
        && (error.Contains("tool not found", StringComparison.OrdinalIgnoreCase) || error.Contains($"tool '{tool}' not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unknown tool", StringComparison.OrdinalIgnoreCase));

    private static RazorpayResult<RazorpayPaymentInfo> ToPayment(RazorpayResult<JsonElement> result, string? orderId) =>
        !result.Success
            ? RazorpayResult<RazorpayPaymentInfo>.Fail(result.Error!)
            : Payment(result.Value, orderId) is { } payment
                ? RazorpayResult<RazorpayPaymentInfo>.Ok(payment)
                : RazorpayResult<RazorpayPaymentInfo>.Fail("Razorpay returned no payment.");

    private static RazorpayPaymentInfo? Payment(JsonElement p, string? orderId) =>
        p.ValueKind == JsonValueKind.Object && Str(p, "id") is { } id
            ? new RazorpayPaymentInfo(id, Str(p, "order_id") ?? orderId, Long(p, "amount") ?? 0, Str(p, "currency") ?? "INR", Str(p, "status") ?? "unknown",
                Str(p, "method"), Long(p, "amount_refunded") ?? 0, Str(p, "error_description"))
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
