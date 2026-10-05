using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Application.Razorpay;

public interface IRazorpayService
{
    Task<RazorpaySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<RazorpaySettingsDto> UpdateSettingsAsync(UpdateRazorpaySettingsRequest request, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Checks a key pair against Razorpay, saved or not. Never throws on a Razorpay error.</summary>
    Task<RazorpayCheckResultDto> TestKeysAsync(TestRazorpayKeysRequest request, CancellationToken cancellationToken = default);

    Task<RazorpayCheckoutDto> CreateOrderAsync(CreateRazorpayOrderRequest request, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>The server-side proof of a checkout: the signature, then Razorpay's own record of the payment (order, amount, status), capturing it
    /// if it is only authorized.</summary>
    Task<RazorpayVerifyResultDto> VerifyAsync(Guid id, VerifyRazorpayPaymentRequest request, CancellationToken cancellationToken = default);

    Task<RazorpayOrderDto> ReportFailureAsync(Guid id, ReportRazorpayFailureRequest request, CancellationToken cancellationToken = default);

    Task<RazorpayOrderDto> RefundAsync(Guid id, RefundRazorpayOrderRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RazorpayOrderDto>> GetOrdersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RazorpayWebhookEventDto>> GetWebhookEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks the signature over the exact bytes received, records the event once, and applies it to the matching order.</summary>
    Task<RazorpayWebhookOutcome> HandleWebhookAsync(byte[] body, string? signature, string? eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Razorpay, end to end, for the platform's test page - deliberately not wired into tenant billing yet. The browser only ever gets an order id and
/// the public key id; whether money moved is decided here, from the checkout signature and Razorpay's own record of the payment, and kept in step
/// by signed webhooks. Keys live only in the AppSettings table (secrets encrypted).
/// </summary>
public class RazorpayService : IRazorpayService
{
    public const string KeyIdKey = "Razorpay:KeyId";
    public const string KeySecretKey = "Razorpay:KeySecret";
    public const string WebhookSecretKey = "Razorpay:WebhookSecret";
    public const string WebhookPath = "/api/v1/webhooks/razorpay";

    public const decimal MinAmount = 1m;
    public const decimal MaxAmount = 500_000m;
    public static readonly IReadOnlyList<string> Currencies = new[] { "INR", "USD" };

    private const int MaxPayloadLength = 8000;
    private const int ListSize = 50;

    private readonly IApplicationDbContext _context;
    private readonly IAppSettingsStore _store;
    private readonly IRazorpayGateway _gateway;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<RazorpayService> _logger;

    public RazorpayService(IApplicationDbContext context, IAppSettingsStore store, IRazorpayGateway gateway, IDateTimeProvider clock, ILogger<RazorpayService> logger)
    {
        _context = context;
        _store = store;
        _gateway = gateway;
        _clock = clock;
        _logger = logger;
    }

    // ── Settings ────────────────────────────────────────────────────────────────────────

    public async Task<RazorpaySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        var keyId = Get(stored, KeyIdKey);
        var secret = Get(stored, KeySecretKey);
        var webhook = Get(stored, WebhookSecretKey);

        return new RazorpaySettingsDto(
            keyId, keyId.Length == 0 ? "test" : RazorpayKeys.ModeOf(keyId),
            secret.Length > 0, Mask(secret), webhook.Length > 0, Mask(webhook),
            keyId.Length > 0 && secret.Length > 0, WebhookPath);
    }

    public async Task<RazorpaySettingsDto> UpdateSettingsAsync(UpdateRazorpaySettingsRequest request, Guid? userId, CancellationToken cancellationToken = default)
    {
        var keyId = (request.KeyId ?? string.Empty).Trim();
        if (keyId.Length > 0 && !RazorpayKeys.KeyIdPattern.IsMatch(keyId))
            throw Invalid(nameof(request.KeyId), "The key id looks like rzp_test_XXXXXXXXXXXXXX (or rzp_live_...), from Razorpay Dashboard > Account & Settings > API Keys.");
        if (request.KeySecret is { Length: > 200 } || request.WebhookSecret is { Length: > 200 })
            throw Invalid(nameof(request.KeySecret), "That secret is too long.");

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [KeyIdKey] = keyId };
        // Only touch a secret when the caller said something about it.
        if (request.KeySecret is not null)
            values[KeySecretKey] = request.KeySecret.Trim();
        if (request.WebhookSecret is not null)
            values[WebhookSecretKey] = request.WebhookSecret.Trim();

        await _store.UpsertAsync(values, userId, cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<RazorpayCheckResultDto> TestKeysAsync(TestRazorpayKeysRequest request, CancellationToken cancellationToken = default)
    {
        var keyId = (request.KeyId ?? string.Empty).Trim();
        if (!RazorpayKeys.KeyIdPattern.IsMatch(keyId))
            throw Invalid(nameof(request.KeyId), "Enter the key id, e.g. rzp_test_XXXXXXXXXXXXXX.");

        var secret = request.KeySecret?.Trim();
        if (string.IsNullOrEmpty(secret))
            secret = Get(await _store.GetAllAsync(cancellationToken), KeySecretKey);
        if (secret.Length == 0)
            throw Invalid(nameof(request.KeySecret), "Enter the key secret to test.");

        var result = await _gateway.PingAsync(new RazorpayCredentials(keyId, secret), cancellationToken);
        return result.Success
            ? new RazorpayCheckResultDto(true, $"Razorpay accepted these {RazorpayKeys.ModeOf(keyId)} keys.")
            : new RazorpayCheckResultDto(false, result.Error ?? "Razorpay did not accept these keys.");
    }

    // ── Orders ──────────────────────────────────────────────────────────────────────────

    public async Task<RazorpayCheckoutDto> CreateOrderAsync(CreateRazorpayOrderRequest request, Guid? userId, CancellationToken cancellationToken = default)
    {
        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!Currencies.Contains(currency))
            throw Invalid(nameof(request.Currency), $"Use one of: {string.Join(", ", Currencies)}.");
        if (request.Amount < MinAmount || request.Amount > MaxAmount)
            throw Invalid(nameof(request.Amount), $"Enter an amount between {MinAmount:0.00} and {MaxAmount:#,##0.00}.");
        if (decimal.Round(request.Amount, 2) != request.Amount)
            throw Invalid(nameof(request.Amount), "Use at most two decimal places.");

        var credentials = await RequireCredentialsAsync(cancellationToken);
        var amountMinor = (long)(request.Amount * 100m);
        var description = Clean(request.Description, 255) ?? "Platform test payment";
        var receipt = $"rzp-test-{_clock.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";

        var notes = new Dictionary<string, string> { ["source"] = "platform-razorpay-test", ["description"] = description };
        var created = await _gateway.CreateOrderAsync(credentials, amountMinor, currency, receipt, notes, cancellationToken);
        if (!created.Success || created.Value is null)
            throw new ConflictException($"Razorpay could not create the order: {created.Error}");

        var order = new RazorpayOrder
        {
            OrderId = created.Value.Id,
            Receipt = receipt,
            AmountMinor = amountMinor,
            Currency = currency,
            Description = description,
            Mode = credentials.Mode,
            CreatedByUserId = userId
        };
        _context.RazorpayOrders.Add(order);
        await _context.SaveChangesAsync(cancellationToken);

        return new RazorpayCheckoutDto(
            order.Id, order.OrderId, credentials.KeyId, order.Mode, amountMinor, currency, "Sales Automation", description,
            new RazorpayPrefillDto(Clean(request.CustomerName, 100), Clean(request.CustomerEmail, 200), Clean(request.CustomerContact, 20)));
    }

    public async Task<RazorpayVerifyResultDto> VerifyAsync(Guid id, VerifyRazorpayPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var order = await FindAsync(id, cancellationToken);
        var steps = new List<RazorpayStepDto>();

        RazorpayVerifyResultDto Done(bool success, string message) => new(success, message, steps, ToDto(order));

        // A replay of a checkout already proven: nothing to redo.
        if (order.SignatureVerifiedAtUtc is not null && order.PaymentId == request.RazorpayPaymentId && Rank(order.Status) >= Rank(RazorpayOrderStatus.Paid))
        {
            steps.Add(new RazorpayStepDto("Already verified", true, $"Paid on {order.PaidAtUtc:u}."));
            return Done(true, "This payment was already verified.");
        }

        if (!string.Equals(request.RazorpayOrderId, order.OrderId, StringComparison.Ordinal))
        {
            steps.Add(new RazorpayStepDto("Order matches", false, $"Checkout returned {request.RazorpayOrderId}, expected {order.OrderId}."));
            return Done(false, "The checkout response is for a different order.");
        }
        steps.Add(new RazorpayStepDto("Order matches", true, order.OrderId));

        var credentials = await RequireCredentialsAsync(cancellationToken);

        if (!RazorpaySignatures.IsValidPaymentSignature(order.OrderId, request.RazorpayPaymentId ?? string.Empty, request.RazorpaySignature, credentials.KeySecret))
        {
            steps.Add(new RazorpayStepDto("Signature checked on the server", false, "HMAC-SHA256(order_id|payment_id) did not match - the response did not come from Razorpay, or was changed."));
            order.FailureReason = "The checkout signature did not match.";
            await _context.SaveChangesAsync(cancellationToken);
            return Done(false, "The payment could not be verified: the signature does not match.");
        }

        steps.Add(new RazorpayStepDto("Signature checked on the server", true, "HMAC-SHA256(order_id|payment_id) matches."));
        order.SignatureVerifiedAtUtc = _clock.UtcNow;
        order.PaymentId = request.RazorpayPaymentId;

        var fetched = await _gateway.FetchPaymentAsync(credentials, request.RazorpayPaymentId!, cancellationToken);
        if (!fetched.Success || fetched.Value is null)
        {
            steps.Add(new RazorpayStepDto("Payment looked up at Razorpay", false, fetched.Error));
            await _context.SaveChangesAsync(cancellationToken);
            return Done(false, "The signature is genuine, but Razorpay's record of the payment could not be read. The webhook will settle it.");
        }

        var payment = fetched.Value;
        steps.Add(new RazorpayStepDto("Payment looked up at Razorpay", true, $"{payment.Status}, {payment.Method ?? "unknown method"}"));
        order.Method = payment.Method;

        if (payment.OrderId != order.OrderId || payment.Amount != order.AmountMinor || !string.Equals(payment.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(new RazorpayStepDto("Amount and order match", false,
                $"Razorpay has {Format(payment.Amount, payment.Currency)} on {payment.OrderId ?? "no order"}; expected {Format(order.AmountMinor, order.Currency)} on {order.OrderId}."));
            order.FailureReason = "The payment does not match the order's amount or currency.";
            Advance(order, RazorpayOrderStatus.Failed);
            await _context.SaveChangesAsync(cancellationToken);
            return Done(false, "The payment does not match this order.");
        }
        steps.Add(new RazorpayStepDto("Amount and order match", true, Format(order.AmountMinor, order.Currency)));

        if (payment.Status == "authorized")
        {
            var captured = await _gateway.CapturePaymentAsync(credentials, payment.Id, order.AmountMinor, order.Currency, cancellationToken);
            if (!captured.Success || captured.Value is null)
            {
                steps.Add(new RazorpayStepDto("Captured", false, captured.Error));
                Advance(order, RazorpayOrderStatus.Authorized);
                await _context.SaveChangesAsync(cancellationToken);
                return Done(false, "The payment is authorized but could not be captured. Razorpay releases an uncaptured payment after a few days.");
            }

            steps.Add(new RazorpayStepDto("Captured", true, "Was authorized only; captured now."));
            payment = captured.Value;
        }

        if (payment.Status is "captured" or "refunded")
        {
            MarkPaid(order);
            order.FailureReason = null;
            await _context.SaveChangesAsync(cancellationToken);
            return Done(true, $"Payment of {Format(order.AmountMinor, order.Currency)} verified and captured.");
        }

        order.FailureReason = payment.ErrorDescription ?? $"Razorpay reports the payment as {payment.Status}.";
        if (payment.Status == "failed")
            Advance(order, RazorpayOrderStatus.Failed);
        await _context.SaveChangesAsync(cancellationToken);
        return Done(false, order.FailureReason);
    }

    public async Task<RazorpayOrderDto> ReportFailureAsync(Guid id, ReportRazorpayFailureRequest request, CancellationToken cancellationToken = default)
    {
        var order = await FindAsync(id, cancellationToken);

        // The browser's word only: recorded so the page can show it, never enough to undo a payment the server proved.
        var reason = string.Join(" - ", new[] { request.Code, request.Description, request.Reason }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (Rank(order.Status) < Rank(RazorpayOrderStatus.Paid))
            order.FailureReason = Clean(reason, 500) ?? "The checkout reported a failure.";
        if (!string.IsNullOrWhiteSpace(request.PaymentId) && order.PaymentId is null)
            order.PaymentId = Clean(request.PaymentId, 40);
        Advance(order, RazorpayOrderStatus.Failed);

        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(order);
    }

    public async Task<RazorpayOrderDto> RefundAsync(Guid id, RefundRazorpayOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = await FindAsync(id, cancellationToken);
        if (order.PaymentId is null || order.Status is not (RazorpayOrderStatus.Paid or RazorpayOrderStatus.PartiallyRefunded))
            throw new ConflictException("Only a captured payment can be refunded.");

        var remaining = order.AmountMinor - order.RefundedMinor;
        long? amountMinor = null;
        if (request.Amount is { } amount)
        {
            if (amount <= 0 || decimal.Round(amount, 2) != amount)
                throw Invalid(nameof(request.Amount), "Enter a positive amount with at most two decimal places.");
            amountMinor = (long)(amount * 100m);
            if (amountMinor > remaining)
                throw Invalid(nameof(request.Amount), $"At most {Format(remaining, order.Currency)} is left to refund.");
        }

        var credentials = await RequireCredentialsAsync(cancellationToken);
        var refund = await _gateway.RefundAsync(credentials, order.PaymentId, amountMinor, cancellationToken);
        if (!refund.Success || refund.Value is null)
            throw new ConflictException($"Razorpay refused the refund: {refund.Error}");

        order.RefundedMinor = Math.Min(order.AmountMinor, order.RefundedMinor + refund.Value.Amount);
        order.LastRefundId = refund.Value.Id;
        Advance(order, order.RefundedMinor >= order.AmountMinor ? RazorpayOrderStatus.Refunded : RazorpayOrderStatus.PartiallyRefunded);

        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(order);
    }

    public async Task<IReadOnlyList<RazorpayOrderDto>> GetOrdersAsync(CancellationToken cancellationToken = default) =>
        (await _context.RazorpayOrders.OrderByDescending(o => o.CreatedAt).Take(ListSize).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<RazorpayWebhookEventDto>> GetWebhookEventsAsync(CancellationToken cancellationToken = default) =>
        await _context.RazorpayWebhookEvents.OrderByDescending(e => e.CreatedAt).Take(ListSize)
            .Select(e => new RazorpayWebhookEventDto(e.Id, e.EventId, e.Event, e.OrderId, e.PaymentId, e.Applied, e.Note, e.CreatedAt))
            .ToListAsync(cancellationToken);

    // ── Webhook ─────────────────────────────────────────────────────────────────────────

    public async Task<RazorpayWebhookOutcome> HandleWebhookAsync(byte[] body, string? signature, string? eventId, CancellationToken cancellationToken = default)
    {
        var secret = Get(await _store.GetAllAsync(cancellationToken), WebhookSecretKey);
        if (secret.Length == 0)
            return RazorpayWebhookOutcome.NotConfigured;
        if (!RazorpaySignatures.IsValidWebhookSignature(body, signature, secret))
            return RazorpayWebhookOutcome.InvalidSignature;

        // Razorpay always sends an event id; without one, the body's own hash keeps a redelivery from applying twice.
        var key = string.IsNullOrWhiteSpace(eventId) ? "sha256:" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()[..40] : eventId.Trim();
        if (key.Length > 100)
            key = key[..100];
        if (await _context.RazorpayWebhookEvents.AnyAsync(e => e.EventId == key, cancellationToken))
            return RazorpayWebhookOutcome.Duplicate;

        var parsed = Parse(body);
        var record = new RazorpayWebhookEvent
        {
            EventId = key,
            Event = parsed.Event,
            OrderId = parsed.OrderId,
            PaymentId = parsed.PaymentId,
            Payload = Truncate(Encoding.UTF8.GetString(body), MaxPayloadLength)
        };

        var order = parsed.OrderId is not null
            ? await _context.RazorpayOrders.FirstOrDefaultAsync(o => o.OrderId == parsed.OrderId, cancellationToken)
            : parsed.PaymentId is not null
                ? await _context.RazorpayOrders.FirstOrDefaultAsync(o => o.PaymentId == parsed.PaymentId, cancellationToken)
                : null;

        if (order is null)
            record.Note = "Not one of the test page's orders.";
        else
            (record.Applied, record.Note) = Apply(order, parsed);

        if (order is not null)
        {
            order.LastWebhookEvent = parsed.Event;
            order.LastWebhookAtUtc = _clock.UtcNow;
        }

        _context.RazorpayWebhookEvents.Add(record);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a parallel redelivery of the same event.
            _context.ResetChangeTracker();
            return RazorpayWebhookOutcome.Duplicate;
        }

        _logger.LogInformation("Razorpay webhook {Event} ({EventId}) for order {OrderId}: {Note}", parsed.Event, key, parsed.OrderId, record.Note);
        return record.Applied ? RazorpayWebhookOutcome.Applied : RazorpayWebhookOutcome.Ignored;
    }

    private (bool Applied, string Note) Apply(RazorpayOrder order, ParsedWebhook w)
    {
        if (w.PaymentId is not null && order.PaymentId is null)
            order.PaymentId = w.PaymentId;
        if (w.Method is not null)
            order.Method ??= w.Method;

        switch (w.Event)
        {
            case "payment.authorized":
                return (Advance(order, RazorpayOrderStatus.Authorized), "Payment authorized.");

            case "payment.captured":
            case "order.paid":
                var wasPaid = Rank(order.Status) >= Rank(RazorpayOrderStatus.Paid);
                MarkPaid(order);
                return (!wasPaid, wasPaid ? "Already paid." : "Payment captured - order paid.");

            case "payment.failed":
                var why = w.ErrorDescription ?? "Payment failed.";
                // An earlier attempt failing after a later one was captured: kept in the log, not shown as the order's problem.
                if (Rank(order.Status) >= Rank(RazorpayOrderStatus.Paid))
                    return (false, $"An earlier attempt failed ({why}); the order is already paid.");
                order.FailureReason = why;
                return (Advance(order, RazorpayOrderStatus.Failed), $"Payment failed: {why}");

            case "refund.created":
            case "refund.processed":
                if (w.RefundId is not null)
                    order.LastRefundId = w.RefundId;
                var refunded = Math.Max(order.RefundedMinor, w.PaymentAmountRefunded ?? 0);
                if (refunded == order.RefundedMinor && w.RefundAmount is { } r && w.PaymentAmountRefunded is null)
                    refunded = Math.Min(order.AmountMinor, order.RefundedMinor + r);
                var changed = refunded != order.RefundedMinor;
                order.RefundedMinor = refunded;
                if (refunded > 0)
                    changed |= Advance(order, refunded >= order.AmountMinor ? RazorpayOrderStatus.Refunded : RazorpayOrderStatus.PartiallyRefunded);
                return (changed, $"Refund {w.Event["refund.".Length..]}: {Format(order.RefundedMinor, order.Currency)} refunded in all.");

            case "refund.failed":
                return (false, "Razorpay could not process a refund.");

            default:
                return (false, $"{w.Event} is recorded but not acted on.");
        }
    }

    private record ParsedWebhook(
        string Event, string? OrderId, string? PaymentId, string? Method, string? ErrorDescription,
        string? RefundId, long? RefundAmount, long? PaymentAmountRefunded);

    private static ParsedWebhook Parse(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = Str(root, "event") ?? "unknown";
            var payload = root.TryGetProperty("payload", out var p) ? p : default;

            JsonElement? Entity(string kind) =>
                payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(kind, out var k) && k.TryGetProperty("entity", out var e) ? e : null;

            var payment = Entity("payment");
            var order = Entity("order");
            var refund = Entity("refund");

            return new ParsedWebhook(
                Truncate(name, 60),
                (payment is { } pay ? Str(pay, "order_id") : null) ?? (order is { } ord ? Str(ord, "id") : null),
                (payment is { } pay2 ? Str(pay2, "id") : null) ?? (refund is { } rf ? Str(rf, "payment_id") : null),
                payment is { } pay3 ? Str(pay3, "method") : null,
                payment is { } pay4 ? Str(pay4, "error_description") : null,
                refund is { } rf2 ? Str(rf2, "id") : null,
                refund is { } rf3 ? Long(rf3, "amount") : null,
                payment is { } pay5 ? Long(pay5, "amount_refunded") : null);
        }
        catch (JsonException)
        {
            return new ParsedWebhook("unparseable", null, null, null, null, null, null, null);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Status only moves forward: a late payment.failed (an earlier attempt) must not undo a payment that was captured, and a retry after
    /// a failed attempt can still succeed on the same order.</summary>
    private static int Rank(RazorpayOrderStatus status) => status switch
    {
        RazorpayOrderStatus.Created => 0,
        RazorpayOrderStatus.Failed => 1,
        RazorpayOrderStatus.Authorized => 2,
        RazorpayOrderStatus.Paid => 3,
        RazorpayOrderStatus.PartiallyRefunded => 4,
        RazorpayOrderStatus.Refunded => 5,
        _ => 0
    };

    private static bool Advance(RazorpayOrder order, RazorpayOrderStatus to)
    {
        if (Rank(to) <= Rank(order.Status))
            return false;
        order.Status = to;
        return true;
    }

    private void MarkPaid(RazorpayOrder order)
    {
        if (Advance(order, RazorpayOrderStatus.Paid) || order.PaidAtUtc is null)
            order.PaidAtUtc ??= _clock.UtcNow;
    }

    private async Task<RazorpayCredentials> RequireCredentialsAsync(CancellationToken cancellationToken)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        var keyId = Get(stored, KeyIdKey);
        var secret = Get(stored, KeySecretKey);
        if (keyId.Length == 0 || secret.Length == 0)
            throw new ConflictException("Razorpay is not set up: save the key id and key secret first.");
        return new RazorpayCredentials(keyId, secret);
    }

    private async Task<RazorpayOrder> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.RazorpayOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(RazorpayOrder), id);

    public static string Format(long minor, string currency) =>
        $"{currency} {(minor / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture)}";

    private static RazorpayOrderDto ToDto(RazorpayOrder o) => new(
        o.Id, o.OrderId, o.Receipt, o.AmountMinor, o.Currency, o.Description, o.Status.ToString(), o.Mode, o.PaymentId, o.Method,
        o.SignatureVerifiedAtUtc, o.PaidAtUtc, o.RefundedMinor, o.LastRefundId, o.FailureReason, o.LastWebhookEvent, o.LastWebhookAtUtc, o.CreatedAt);

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static string? Mask(string value) =>
        value.Length == 0 ? null : value.Length <= 4 ? "••••" : $"••••{value[^4..]}";

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Truncate(trimmed, max);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
