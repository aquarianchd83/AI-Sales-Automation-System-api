using System.Text.Json;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Infrastructure.Payments;

/// <summary>
/// The backend's Razorpay gateway. By default orders, payments, capture and refunds go through Razorpay's MCP server (<see cref="RazorpayMcpClient"/>):
/// <c>create_order</c>, <c>fetch_payment</c>, <c>fetch_order_payments</c>, <c>capture_payment</c>, <c>create_refund</c> and <c>fetch_all_orders</c>
/// (the key check). Razorpay's MCP server has no tools for plans, subscriptions or invoices, so UPI AutoPay keeps using the REST API
/// (<see cref="RazorpayGateway"/>) for those.
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
        return result.Success ? RazorpayResult<bool>.Ok(true) : RazorpayResult<bool>.Fail(result.Error!);
    }

    public async Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
        RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.CreateOrderAsync(credentials, amountMinor, currency, receipt, notes, cancellationToken);

        var result = await CallAsync(credentials, "create_order", new Dictionary<string, object?>
        {
            ["amount"] = amountMinor,
            ["currency"] = currency,
            ["receipt"] = receipt,
            ["notes"] = notes
        }, cancellationToken);
        if (!result.Success)
            return RazorpayResult<RazorpayOrderInfo>.Fail(result.Error!);

        var o = result.Value;
        return Str(o, "id") is { } id
            ? RazorpayResult<RazorpayOrderInfo>.Ok(new RazorpayOrderInfo(id, Long(o, "amount") ?? amountMinor, Str(o, "currency") ?? currency, Str(o, "receipt") ?? receipt, Str(o, "status") ?? "created"))
            : RazorpayResult<RazorpayOrderInfo>.Fail("Razorpay returned no order id.");
    }

    public async Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.FetchPaymentAsync(credentials, paymentId, cancellationToken);

        return ToPayment(await CallAsync(credentials, "fetch_payment", new Dictionary<string, object?> { ["payment_id"] = paymentId }, cancellationToken), null);
    }

    public async Task<RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>> FetchOrderPaymentsAsync(
        RazorpayCredentials credentials, string orderId, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.FetchOrderPaymentsAsync(credentials, orderId, cancellationToken);

        var result = await CallAsync(credentials, "fetch_order_payments", new Dictionary<string, object?> { ["order_id"] = orderId }, cancellationToken);
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

    public async Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
        RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.CapturePaymentAsync(credentials, paymentId, amountMinor, currency, cancellationToken);

        return ToPayment(await CallAsync(credentials, "capture_payment", new Dictionary<string, object?>
        {
            ["payment_id"] = paymentId,
            ["amount"] = amountMinor,
            ["currency"] = currency
        }, cancellationToken), null);
    }

    public async Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default)
    {
        if (await RestAsync(cancellationToken))
            return await _rest.RefundAsync(credentials, paymentId, amountMinor, cancellationToken);

        // create_refund needs an amount; "refund in full" means whatever of the payment is not refunded yet.
        var amount = amountMinor;
        if (amount is null)
        {
            var payment = await FetchPaymentAsync(credentials, paymentId, cancellationToken);
            if (!payment.Success || payment.Value is null)
                return RazorpayResult<RazorpayRefundInfo>.Fail(payment.Error ?? "Razorpay returned no payment.");
            amount = payment.Value.Amount - payment.Value.AmountRefunded;
            if (amount <= 0)
                return RazorpayResult<RazorpayRefundInfo>.Fail("The payment is already fully refunded.");
        }

        var result = await CallAsync(credentials, "create_refund", new Dictionary<string, object?> { ["payment_id"] = paymentId, ["amount"] = amount }, cancellationToken);
        if (!result.Success)
            return RazorpayResult<RazorpayRefundInfo>.Fail(result.Error!);

        var r = result.Value;
        return Str(r, "id") is { } id
            ? RazorpayResult<RazorpayRefundInfo>.Ok(new RazorpayRefundInfo(id, Str(r, "payment_id") ?? paymentId, Long(r, "amount") ?? amount.Value, Str(r, "status") ?? "pending"))
            : RazorpayResult<RazorpayRefundInfo>.Fail("Razorpay returned no refund id.");
    }

    // ── Not offered by Razorpay's MCP server: always the REST API ────────────────────────────

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
