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
using WhatsAppSalesAutomation.Application.Wallet;
using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Application.Razorpay;

public interface IRazorpayService
{
    Task<RazorpaySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<RazorpaySettingsDto> UpdateSettingsAsync(UpdateRazorpaySettingsRequest request, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Switches UPI AutoPay on or off for tenants. Turning it on needs the key id, key secret and webhook secret saved: without them a tenant
    /// would be sent to Razorpay to authorise a mandate whose payments this platform could never confirm. Turning it off is always allowed.</summary>
    Task<RazorpaySettingsDto> SetAutoPayEnabledAsync(bool enabled, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Chooses how payments and orders are read: "Mcp" (Razorpay's MCP server) or "Rest". Writes always use the REST API.</summary>
    Task<RazorpaySettingsDto> SetReadTransportAsync(string transport, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Chooses which saved key set - "test" or "live" - new payments use. Needs that set's key id and key secret, and for Live the webhook
    /// secret too: live money must be confirmable. Orders, top-ups and mandates already created keep the keys of the mode they were made in.</summary>
    Task<RazorpaySettingsDto> SetActiveModeAsync(string mode, Guid? userId, CancellationToken cancellationToken = default);

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
    // The keys themselves live in two slots, Test and Live, plus the active mode: see RazorpayKeyStore.
    // Read by RazorpayMcpGateway (Infrastructure), which names the same key.
    public const string TransportKey = "Razorpay:Transport";
    public const string TransportMcp = "Mcp";
    public const string TransportRest = "Rest";
    public const string WebhookPath = "/api/v1/webhooks/razorpay";

    public const decimal MinAmount = 1m;
    public const decimal MaxAmount = 500_000m;
    public static readonly IReadOnlyList<string> Currencies = new[] { "INR", "USD" };

    private const int ListSize = 50;

    private readonly IApplicationDbContext _context;
    private readonly IAppSettingsStore _store;
    private readonly IRazorpayGateway _gateway;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<RazorpayService> _logger;
    /// <summary>Optional so the test page can be exercised without a job server; DI always supplies one.</summary>
    private readonly IRazorpayWebhookQueue? _queue;
    /// <summary>Optional so the test page and its tests run without the tenant wallet; DI always supplies it. Tenants' wallet top-up orders are applied through it.</summary>
    private readonly IWalletRechargeService? _walletRecharge;

    public RazorpayService(IApplicationDbContext context, IAppSettingsStore store, IRazorpayGateway gateway, IDateTimeProvider clock, ILogger<RazorpayService> logger,
        IRazorpayWebhookQueue? queue = null, IWalletRechargeService? walletRecharge = null)
    {
        _queue = queue;
        _walletRecharge = walletRecharge;
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
        var active = RazorpayKeyStore.Active(stored);
        var autoPay = string.Equals(Get(stored, Billing.AutoPay.AutoPaySettings.EnabledKey), "true", StringComparison.OrdinalIgnoreCase);
        var transport = string.Equals(Get(stored, TransportKey), TransportRest, StringComparison.OrdinalIgnoreCase) ? TransportRest : TransportMcp;

        return new RazorpaySettingsDto(
            active.KeyId, active.Mode,
            active.KeySecret.Length > 0, Mask(active.KeySecret), active.WebhookSecret.Length > 0, Mask(active.WebhookSecret),
            active.IsConfigured, WebhookPath,
            autoPay, autoPay && active.IsComplete, transport,
            active.Mode,
            ToDto(RazorpayKeyStore.For(stored, RazorpayKeyStore.Test)),
            ToDto(RazorpayKeyStore.For(stored, RazorpayKeyStore.Live)));
    }

    public async Task<RazorpaySettingsDto> UpdateSettingsAsync(UpdateRazorpaySettingsRequest request, Guid? userId, CancellationToken cancellationToken = default)
    {
        var keyId = (request.KeyId ?? string.Empty).Trim();
        if (keyId.Length > 0 && !RazorpayKeys.KeyIdPattern.IsMatch(keyId))
            throw Invalid(nameof(request.KeyId), "The key id looks like rzp_test_XXXXXXXXXXXXXX (or rzp_live_...), from Razorpay Dashboard > Account & Settings > API Keys.");
        if (request.KeySecret is { Length: > 200 } || request.WebhookSecret is { Length: > 200 })
            throw Invalid(nameof(request.KeySecret), "That secret is too long.");

        string mode;
        if (request.Mode is null)
            mode = keyId.Length > 0 ? RazorpayKeys.ModeOf(keyId) : RazorpayKeyStore.ActiveMode(await _store.GetAllAsync(cancellationToken));
        else
            mode = RazorpayKeyStore.Normalize(request.Mode) ?? throw Invalid(nameof(request.Mode), "Choose \"test\" or \"live\".");
        if (keyId.Length > 0 && RazorpayKeys.ModeOf(keyId) != mode)
            throw Invalid(nameof(request.KeyId), mode == RazorpayKeyStore.Live
                ? "Live keys start with rzp_live_. Save a rzp_test_ key under Test keys."
                : "Test keys start with rzp_test_. Save a rzp_live_ key under Live keys.");

        var stored = await _store.GetAllAsync(cancellationToken);
        var values = RazorpayKeyStore.LegacyMigration(stored);
        values[RazorpayKeyStore.KeyIdKey(mode)] = keyId;
        // Only touch a secret when the caller said something about it.
        if (request.KeySecret is not null)
            values[RazorpayKeyStore.KeySecretKey(mode)] = request.KeySecret.Trim();
        if (request.WebhookSecret is not null)
            values[RazorpayKeyStore.WebhookSecretKey(mode)] = request.WebhookSecret.Trim();

        // Emptying the keys of the mode in use would leave payments with nothing to run on while AutoPay is switched on; refuse instead.
        var activeMode = values.TryGetValue(RazorpayKeyStore.ActiveModeKey, out var am) ? am! : RazorpayKeyStore.ActiveMode(stored);
        if (mode == activeMode && mode == RazorpayKeyStore.Live)
        {
            var after = RazorpayKeyStore.For(Merge(stored, values), mode);
            if (!after.IsComplete)
                throw new ConflictException("Live mode is in use. Switch to Test before removing a Live key.");
        }

        await _store.UpsertAsync(values, userId, cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<RazorpaySettingsDto> SetActiveModeAsync(string mode, Guid? userId, CancellationToken cancellationToken = default)
    {
        var target = RazorpayKeyStore.Normalize(mode) ?? throw Invalid(nameof(mode), "Choose \"test\" or \"live\".");
        var stored = await _store.GetAllAsync(cancellationToken);
        var keys = RazorpayKeyStore.For(stored, target);
        if (!keys.IsConfigured)
            throw new ConflictException($"Save the {target} key id and key secret first.");
        if (target == RazorpayKeyStore.Live && !keys.IsComplete)
            throw new ConflictException("Save the live webhook secret first: without it live payments cannot be confirmed.");

        var values = RazorpayKeyStore.LegacyMigration(stored);
        values[RazorpayKeyStore.ActiveModeKey] = target;
        await _store.UpsertAsync(values, userId, cancellationToken);
        _logger.LogWarning("Razorpay active mode switched to {Mode} by {UserId}", target, userId);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<RazorpaySettingsDto> SetAutoPayEnabledAsync(bool enabled, Guid? userId, CancellationToken cancellationToken = default)
    {
        if (enabled)
        {
            var current = await GetSettingsAsync(cancellationToken);
            if (!current.IsConfigured)
                throw new ConflictException("Save the Razorpay key id and key secret first.");
            if (!current.HasWebhookSecret)
                throw new ConflictException("Save the webhook secret first: without it payments cannot be confirmed, so tenants would be sent to Razorpay for nothing.");
        }

        await _store.UpsertAsync(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [Billing.AutoPay.AutoPaySettings.EnabledKey] = enabled ? "true" : "false" }, userId, cancellationToken);
        _logger.LogInformation("UPI AutoPay switched {State} by {UserId}", enabled ? "on" : "off", userId);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<RazorpaySettingsDto> SetReadTransportAsync(string transport, Guid? userId, CancellationToken cancellationToken = default)
    {
        var value = (transport ?? string.Empty).Trim();
        value = string.Equals(value, TransportRest, StringComparison.OrdinalIgnoreCase) ? TransportRest
            : string.Equals(value, TransportMcp, StringComparison.OrdinalIgnoreCase) ? TransportMcp
            : throw Invalid(nameof(transport), "Choose \"Mcp\" or \"Rest\".");

        await _store.UpsertAsync(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [TransportKey] = value }, userId, cancellationToken);
        _logger.LogInformation("Razorpay reads switched to {Transport} by {UserId}", value, userId);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<RazorpayCheckResultDto> TestKeysAsync(TestRazorpayKeysRequest request, CancellationToken cancellationToken = default)
    {
        var keyId = (request.KeyId ?? string.Empty).Trim();
        if (!RazorpayKeys.KeyIdPattern.IsMatch(keyId))
            throw Invalid(nameof(request.KeyId), "Enter the key id, e.g. rzp_test_XXXXXXXXXXXXXX.");

        var secret = request.KeySecret?.Trim();
        if (string.IsNullOrEmpty(secret))
            secret = RazorpayKeyStore.For(await _store.GetAllAsync(cancellationToken), RazorpayKeys.ModeOf(keyId)).KeySecret;
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

        var credentials = await RequireCredentialsAsync(order.Mode, cancellationToken);

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

        var credentials = await RequireCredentialsAsync(order.Mode, cancellationToken);
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
        // Test and Live webhooks arrive at the same URL, each signed with its own mode's secret: the active one is tried first.
        var secrets = RazorpayKeyStore.WebhookSecrets(await _store.GetAllAsync(cancellationToken));
        if (secrets.Count == 0)
            return RazorpayWebhookOutcome.NotConfigured;
        if (!secrets.Any(x => RazorpaySignatures.IsValidWebhookSignature(body, signature, x.Secret)))
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
            SubscriptionId = parsed.SubscriptionId,
            EventCreatedAtUtc = parsed.CreatedAtUtc,
            // The whole body (the endpoint caps its size): a subscription event is applied from this column, now or on a retry.
            Payload = Encoding.UTF8.GetString(body)
        };

        if (parsed.Event.StartsWith(SubscriptionEventPrefix, StringComparison.Ordinal))
            return await StoreForProcessingAsync(record, cancellationToken);

        // A tenant's wallet top-up is applied by the wallet service (it credits the wallet, exactly once); anything it does not recognise falls through.
        if (_walletRecharge is not null && (parsed.OrderId is not null || parsed.PaymentId is not null))
        {
            var wallet = await _walletRecharge.HandleWebhookAsync(
                new WalletWebhookEvent(parsed.Event, parsed.OrderId, parsed.PaymentId, parsed.Method, parsed.ErrorDescription, parsed.PaymentAmount,
                    parsed.PaymentCurrency, parsed.PaymentStatus, parsed.RefundId, parsed.RefundAmount, parsed.PaymentAmountRefunded), cancellationToken);
            if (wallet.Matched)
            {
                record.Applied = wallet.Applied;
                record.Note = Truncate(wallet.Note, 500);
                record.ProcessingStatus = wallet.Applied ? RazorpayWebhookProcessingStatus.Completed : RazorpayWebhookProcessingStatus.Ignored;
                record.ProcessedAtUtc = _clock.UtcNow;
                _context.RazorpayWebhookEvents.Add(record);
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Lost a race with a parallel redelivery of the same event; the wallet itself was credited only once.
                    _context.ResetChangeTracker();
                    return RazorpayWebhookOutcome.Duplicate;
                }

                _logger.LogInformation("Razorpay webhook {Event} ({EventId}) for wallet top-up order {OrderId}: {Note}", parsed.Event, key, parsed.OrderId, record.Note);
                return record.Applied ? RazorpayWebhookOutcome.Applied : RazorpayWebhookOutcome.Ignored;
            }
        }

        var order = parsed.OrderId is not null
            ? await _context.RazorpayOrders.FirstOrDefaultAsync(o => o.OrderId == parsed.OrderId, cancellationToken)
            : parsed.PaymentId is not null
                ? await _context.RazorpayOrders.FirstOrDefaultAsync(o => o.PaymentId == parsed.PaymentId, cancellationToken)
                : null;

        // A failed payment that belongs to an invoice, not to a test order, is a subscription's debit failing: stored and applied by the job.
        if (order is null && parsed.Event == PaymentFailedEvent && parsed.InvoiceId is not null)
            return await StoreForProcessingAsync(record, cancellationToken);

        if (order is null)
            record.Note = "Not one of the test page's orders.";
        else
            (record.Applied, record.Note) = Apply(order, parsed);

        if (order is not null)
        {
            order.LastWebhookEvent = parsed.Event;
            order.LastWebhookAtUtc = _clock.UtcNow;
        }

        // Applied here, in the same save that stores it, so there is nothing left to process afterwards.
        record.ProcessingStatus = record.Applied ? RazorpayWebhookProcessingStatus.Completed : RazorpayWebhookProcessingStatus.Ignored;
        record.ProcessedAtUtc = _clock.UtcNow;

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

    public const string SubscriptionEventPrefix = "subscription.";
    public const string PaymentFailedEvent = "payment.failed";

    /// <summary>A subscription event is only stored here - durably, before Razorpay is told it arrived - and applied afterwards by the
    /// background job. Plans and credits hang on these events, so one must never be lost because applying it failed.</summary>
    private async Task<RazorpayWebhookOutcome> StoreForProcessingAsync(RazorpayWebhookEvent record, CancellationToken cancellationToken)
    {
        record.ProcessingStatus = RazorpayWebhookProcessingStatus.Received;
        record.Note = "Stored; waiting to be applied.";
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

        try
        {
            _queue?.Enqueue(record.Id);
        }
        catch (Exception ex)
        {
            // It is stored, which is what matters: the retry pass applies anything the queue never took.
            _logger.LogWarning(ex, "Razorpay webhook {Event} ({EventId}) was stored but could not be queued; the retry pass will apply it", record.Event, record.EventId);
        }

        _logger.LogInformation("Razorpay webhook {Event} ({EventId}) for subscription {SubscriptionId} stored for processing", record.Event, record.EventId, record.SubscriptionId);
        return RazorpayWebhookOutcome.Queued;
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
        string? RefundId, long? RefundAmount, long? PaymentAmountRefunded,
        string? SubscriptionId = null, DateTime? CreatedAtUtc = null, string? InvoiceId = null,
        long? PaymentAmount = null, string? PaymentCurrency = null, string? PaymentStatus = null);

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
            var subscription = Entity("subscription");

            return new ParsedWebhook(
                Truncate(name, 60),
                (payment is { } pay ? Str(pay, "order_id") : null) ?? (order is { } ord ? Str(ord, "id") : null),
                (payment is { } pay2 ? Str(pay2, "id") : null) ?? (refund is { } rf ? Str(rf, "payment_id") : null),
                payment is { } pay3 ? Str(pay3, "method") : null,
                payment is { } pay4 ? Str(pay4, "error_description") : null,
                refund is { } rf2 ? Str(rf2, "id") : null,
                refund is { } rf3 ? Long(rf3, "amount") : null,
                payment is { } pay5 ? Long(pay5, "amount_refunded") : null,
                subscription is { } sub ? Str(sub, "id") is { } subId ? Truncate(subId, 40) : null : null,
                Long(root, "created_at") is { } createdAt and > 0 ? DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime : null,
                payment is { } pay6 ? Str(pay6, "invoice_id") : null,
                payment is { } pay7 ? Long(pay7, "amount") : null,
                payment is { } pay8 ? Str(pay8, "currency") : null,
                payment is { } pay9 ? Str(pay9, "status") : null);
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

    /// <summary>The active mode's keys, or - for an order already made - the keys of the mode it was made in.</summary>
    private async Task<RazorpayCredentials> RequireCredentialsAsync(CancellationToken cancellationToken) =>
        await RequireCredentialsAsync(null, cancellationToken);

    private async Task<RazorpayCredentials> RequireCredentialsAsync(string? mode, CancellationToken cancellationToken)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        return RazorpayKeyStore.CredentialsFor(stored, mode)
            ?? throw new ConflictException(mode is null
                ? "Razorpay is not set up: save the key id and key secret first."
                : $"This order was made with {mode} keys, and no {mode} key id and key secret are saved now.");
    }

    private static RazorpayKeySetDto ToDto(RazorpayKeySet k) => new(
        k.Mode, k.KeyId, k.KeySecret.Length > 0, Mask(k.KeySecret), k.WebhookSecret.Length > 0, Mask(k.WebhookSecret), k.IsConfigured, k.IsComplete);

    private static IReadOnlyDictionary<string, string?> Merge(IReadOnlyDictionary<string, string?> stored, IReadOnlyDictionary<string, string?> values)
    {
        var merged = new Dictionary<string, string?>(stored, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
            merged[k] = v;
        return merged;
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
