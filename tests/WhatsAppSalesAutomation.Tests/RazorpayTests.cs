using System.Net;
using System.Text;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Infrastructure.Payments;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Razorpay's two HMAC-SHA256 signatures: the checkout's over "order_id|payment_id" with the key secret, the webhook's over the raw body
/// with the webhook secret.</summary>
public class RazorpaySignatureTests
{
    [Fact]
    public void Hmac_sha256_matches_the_well_known_vector()
    {
        Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            RazorpaySignatures.Compute("key", Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog")));
    }

    [Fact]
    public void The_checkout_signature_is_over_order_id_pipe_payment_id()
    {
        var expected = RazorpaySignatures.Compute("s3cret", Encoding.UTF8.GetBytes("order_ABC|pay_XYZ"));

        Assert.Equal(expected, RazorpaySignatures.PaymentSignature("order_ABC", "pay_XYZ", "s3cret"));
        Assert.True(RazorpaySignatures.IsValidPaymentSignature("order_ABC", "pay_XYZ", expected, "s3cret"));
        Assert.True(RazorpaySignatures.IsValidPaymentSignature("order_ABC", "pay_XYZ", expected.ToUpperInvariant(), "s3cret"));
        Assert.False(RazorpaySignatures.IsValidPaymentSignature("order_ABC", "pay_OTHER", expected, "s3cret"));
        Assert.False(RazorpaySignatures.IsValidPaymentSignature("order_ABC", "pay_XYZ", expected, "other-secret"));
        Assert.False(RazorpaySignatures.IsValidPaymentSignature("order_ABC", "pay_XYZ", null, "s3cret"));
        Assert.False(RazorpaySignatures.IsValidPaymentSignature("order_ABC", "pay_XYZ", "abc", "s3cret"));
    }

    [Fact]
    public void A_webhook_needs_a_secret_and_the_exact_bytes()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"payment.captured\"}");
        var signature = RazorpaySignatures.Compute("whsec", body);

        Assert.True(RazorpaySignatures.IsValidWebhookSignature(body, signature, "whsec"));
        Assert.False(RazorpaySignatures.IsValidWebhookSignature(Encoding.UTF8.GetBytes("{\"event\": \"payment.captured\"}"), signature, "whsec"));
        Assert.False(RazorpaySignatures.IsValidWebhookSignature(body, signature, ""));
    }

    [Theory]
    [InlineData("rzp_test_1DP5mmOlF5G5ag", true, "test")]
    [InlineData("rzp_live_ILgsfZCZoFIKMb", true, "live")]
    [InlineData("rzp_1DP5mmOlF5G5ag", false, "test")]
    [InlineData("pk_test_123456", false, "test")]
    public void Key_ids_are_recognised_with_their_mode(string keyId, bool valid, string mode)
    {
        Assert.Equal(valid, RazorpayKeys.KeyIdPattern.IsMatch(keyId));
        Assert.Equal(mode, RazorpayKeys.ModeOf(keyId));
    }
}

/// <summary>The test page's flow: keys, an order, the checkout proven on the server (signature, then Razorpay's own record), refunds, and webhooks
/// applied once and never backwards.</summary>
public sealed class RazorpayServiceTests : IDisposable
{
    private const string KeyId = "rzp_test_1DP5mmOlF5G5ag";
    private const string KeySecret = "key-secret-123";
    private const string WebhookSecret = "webhook-secret-456";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly MemoryStore _store = new();
    private readonly FakeGateway _gateway = new();
    private readonly RazorpayService _service;

    public RazorpayServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _service = new RazorpayService(_db, _store, _gateway, new TestClock(), NullLogger<RazorpayService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Configure()
    {
        _store.Rows[RazorpayService.KeyIdKey] = KeyId;
        _store.Rows[RazorpayService.KeySecretKey] = KeySecret;
        _store.Rows[RazorpayService.WebhookSecretKey] = WebhookSecret;
    }

    private async Task<RazorpayCheckoutDto> OrderAsync(decimal amount = 499.50m)
    {
        Configure();
        return await _service.CreateOrderAsync(new CreateRazorpayOrderRequest(amount, "INR", "Test", "Asha", "asha@example.com", "+919876543210"), Guid.NewGuid());
    }

    private static VerifyRazorpayPaymentRequest Signed(string orderId, string paymentId) =>
        new(orderId, paymentId, RazorpaySignatures.PaymentSignature(orderId, paymentId, KeySecret));

    // ── Settings ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_are_stored_with_secrets_masked_and_kept_when_not_resent()
    {
        var saved = await _service.UpdateSettingsAsync(new UpdateRazorpaySettingsRequest(KeyId, "abcdefgh1234", "wh-9876"), null);

        Assert.True(saved.IsConfigured);
        Assert.Equal("test", saved.Mode);
        Assert.Equal("••••1234", saved.KeySecretHint);
        Assert.Equal("••••9876", saved.WebhookSecretHint);
        Assert.Equal("/api/v1/webhooks/razorpay", saved.WebhookPath);

        var kept = await _service.UpdateSettingsAsync(new UpdateRazorpaySettingsRequest("rzp_live_ILgsfZCZoFIKMb", null, ""), null);
        Assert.Equal("live", kept.Mode);
        Assert.Equal("abcdefgh1234", _store.Rows[RazorpayService.KeySecretKey]);
        Assert.False(kept.HasWebhookSecret);
    }

    [Fact]
    public async Task A_key_id_that_is_not_razorpays_is_refused()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateSettingsAsync(new UpdateRazorpaySettingsRequest("sk_live_abc", null, null), null));
    }

    [Fact]
    public async Task Testing_keys_uses_the_stored_secret_when_none_is_typed_and_reports_razorpays_answer()
    {
        Configure();
        Assert.True((await _service.TestKeysAsync(new TestRazorpayKeysRequest(KeyId, null))).Success);
        Assert.Equal(KeySecret, _gateway.LastCredentials!.KeySecret);

        _gateway.PingError = "Authentication failed";
        var failed = await _service.TestKeysAsync(new TestRazorpayKeysRequest(KeyId, "wrong"));
        Assert.False(failed.Success);
        Assert.Equal("Authentication failed", failed.Message);
    }

    // ── Orders ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_order_is_created_in_paise_and_returned_ready_for_checkout()
    {
        var checkout = await OrderAsync(123.45m);

        Assert.Equal(12345, checkout.Amount);
        Assert.Equal(12345, _gateway.LastOrderAmount);
        Assert.Equal(KeyId, checkout.KeyId);
        Assert.Equal("asha@example.com", checkout.Prefill.Email);

        var row = await _db.RazorpayOrders.SingleAsync();
        Assert.Equal(checkout.OrderId, row.OrderId);
        Assert.Equal(RazorpayOrderStatus.Created, row.Status);
        Assert.True(row.Receipt.Length <= 40);
        Assert.Equal("platform-razorpay-test", _gateway.LastNotes!["source"]);
    }

    [Theory]
    [InlineData(0.5, "INR")]
    [InlineData(500001, "INR")]
    [InlineData(10.555, "INR")]
    [InlineData(10, "EUR")]
    public async Task Amounts_and_currencies_outside_the_range_are_refused(double amount, string currency)
    {
        Configure();
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateOrderAsync(new CreateRazorpayOrderRequest((decimal)amount, currency, null, null, null, null), null));
    }

    [Fact]
    public async Task Without_keys_no_order_is_made()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateOrderAsync(new CreateRazorpayOrderRequest(10, "INR", null, null, null, null), null));
    }

    // ── Verifying a checkout ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_genuine_checkout_for_a_captured_payment_marks_the_order_paid()
    {
        var checkout = await OrderAsync();
        _gateway.Payments["pay_1"] = new RazorpayPaymentInfo("pay_1", checkout.OrderId, checkout.Amount, "INR", "captured", "upi", 0, null);

        var result = await _service.VerifyAsync(checkout.Id, Signed(checkout.OrderId, "pay_1"));

        Assert.True(result.Success);
        Assert.All(result.Steps, s => Assert.True(s.Ok));
        Assert.Equal("Paid", result.Order.Status);
        Assert.Equal("upi", result.Order.Method);
        Assert.NotNull(result.Order.SignatureVerifiedAtUtc);

        // A second call with the same response is recognised, not redone.
        var again = await _service.VerifyAsync(checkout.Id, Signed(checkout.OrderId, "pay_1"));
        Assert.True(again.Success);
        Assert.Equal("This payment was already verified.", again.Message);
    }

    [Fact]
    public async Task A_forged_signature_is_refused_and_nothing_is_paid()
    {
        var checkout = await OrderAsync();
        _gateway.Payments["pay_1"] = new RazorpayPaymentInfo("pay_1", checkout.OrderId, checkout.Amount, "INR", "captured", "card", 0, null);

        var result = await _service.VerifyAsync(checkout.Id, new VerifyRazorpayPaymentRequest(checkout.OrderId, "pay_1", new string('a', 64)));

        Assert.False(result.Success);
        Assert.Equal("Created", result.Order.Status);
        Assert.Null(result.Order.SignatureVerifiedAtUtc);
        Assert.Contains(result.Steps, s => !s.Ok && s.Label.StartsWith("Signature"));
        Assert.Equal(0, _gateway.FetchCalls);
    }

    [Fact]
    public async Task A_response_for_another_order_is_refused()
    {
        var checkout = await OrderAsync();

        var result = await _service.VerifyAsync(checkout.Id, Signed("order_SOMEONE_ELSE", "pay_1"));

        Assert.False(result.Success);
        Assert.Contains("different order", result.Message);
    }

    [Fact]
    public async Task A_payment_whose_amount_does_not_match_the_order_is_not_accepted()
    {
        var checkout = await OrderAsync();
        _gateway.Payments["pay_1"] = new RazorpayPaymentInfo("pay_1", checkout.OrderId, 100, "INR", "captured", "card", 0, null);

        var result = await _service.VerifyAsync(checkout.Id, Signed(checkout.OrderId, "pay_1"));

        Assert.False(result.Success);
        Assert.Equal("Failed", result.Order.Status);
    }

    [Fact]
    public async Task An_authorized_payment_is_captured()
    {
        var checkout = await OrderAsync();
        _gateway.Payments["pay_1"] = new RazorpayPaymentInfo("pay_1", checkout.OrderId, checkout.Amount, "INR", "authorized", "card", 0, null);

        var result = await _service.VerifyAsync(checkout.Id, Signed(checkout.OrderId, "pay_1"));

        Assert.True(result.Success);
        Assert.Equal(1, _gateway.CaptureCalls);
        Assert.Equal("Paid", result.Order.Status);
        Assert.Contains(result.Steps, s => s.Label == "Captured" && s.Ok);
    }

    [Fact]
    public async Task A_failure_from_the_browser_is_recorded_but_cannot_undo_a_proven_payment()
    {
        var checkout = await OrderAsync();
        var failed = await _service.ReportFailureAsync(checkout.Id, new ReportRazorpayFailureRequest("pay_0", "BAD_REQUEST_ERROR", "Payment failed", "payment_failed"));
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("Payment failed", failed.FailureReason);

        // A retry on the same order succeeds.
        _gateway.Payments["pay_1"] = new RazorpayPaymentInfo("pay_1", checkout.OrderId, checkout.Amount, "INR", "captured", "card", 0, null);
        Assert.Equal("Paid", (await _service.VerifyAsync(checkout.Id, Signed(checkout.OrderId, "pay_1"))).Order.Status);

        Assert.Equal("Paid", (await _service.ReportFailureAsync(checkout.Id, new ReportRazorpayFailureRequest(null, "X", "late", null))).Status);
    }

    // ── Refunds ─────────────────────────────────────────────────────────────────────────

    private async Task<RazorpayCheckoutDto> PaidOrderAsync()
    {
        var checkout = await OrderAsync(100m);
        _gateway.Payments["pay_1"] = new RazorpayPaymentInfo("pay_1", checkout.OrderId, checkout.Amount, "INR", "captured", "card", 0, null);
        await _service.VerifyAsync(checkout.Id, Signed(checkout.OrderId, "pay_1"));
        return checkout;
    }

    [Fact]
    public async Task Refunds_can_be_partial_then_full_but_never_more_than_was_paid()
    {
        var checkout = await PaidOrderAsync();

        var partial = await _service.RefundAsync(checkout.Id, new RefundRazorpayOrderRequest(40m));
        Assert.Equal("PartiallyRefunded", partial.Status);
        Assert.Equal(4000, partial.RefundedMinor);
        Assert.Equal(4000, _gateway.LastRefundAmount);

        await Assert.ThrowsAsync<ValidationException>(() => _service.RefundAsync(checkout.Id, new RefundRazorpayOrderRequest(60.01m)));

        var rest = await _service.RefundAsync(checkout.Id, new RefundRazorpayOrderRequest(null));
        Assert.Equal("Refunded", rest.Status);
        Assert.Equal(10000, rest.RefundedMinor);
        Assert.Null(_gateway.LastRefundAmount);
    }

    [Fact]
    public async Task An_unpaid_order_cannot_be_refunded()
    {
        var checkout = await OrderAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _service.RefundAsync(checkout.Id, new RefundRazorpayOrderRequest(null)));
    }

    // ── Webhooks ────────────────────────────────────────────────────────────────────────

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    private Task<RazorpayWebhookOutcome> DeliverAsync(string json, string eventId, string? secret = WebhookSecret)
    {
        var body = Body(json);
        return _service.HandleWebhookAsync(body, secret is null ? null : RazorpaySignatures.Compute(secret, body), eventId);
    }

    private static string PaymentEvent(string name, string orderId, string paymentId, string status, string? error = null) =>
        $"{{\"entity\":\"event\",\"event\":\"{name}\",\"payload\":{{\"payment\":{{\"entity\":{{\"id\":\"{paymentId}\",\"order_id\":\"{orderId}\",\"amount\":49950,\"currency\":\"INR\",\"status\":\"{status}\",\"method\":\"netbanking\"" +
        (error is null ? "" : $",\"error_description\":\"{error}\"") + "}}}}";

    [Fact]
    public async Task Nothing_is_trusted_without_a_secret_or_with_a_wrong_signature()
    {
        var checkout = await OrderAsync();
        _store.Rows[RazorpayService.WebhookSecretKey] = "";
        Assert.Equal(RazorpayWebhookOutcome.NotConfigured, await DeliverAsync(PaymentEvent("payment.captured", checkout.OrderId, "pay_1", "captured"), "evt_1"));

        _store.Rows[RazorpayService.WebhookSecretKey] = WebhookSecret;
        Assert.Equal(RazorpayWebhookOutcome.InvalidSignature, await DeliverAsync(PaymentEvent("payment.captured", checkout.OrderId, "pay_1", "captured"), "evt_1", "not-the-secret"));

        Assert.Empty(await _db.RazorpayWebhookEvents.ToListAsync());
        Assert.Equal(RazorpayOrderStatus.Created, (await _db.RazorpayOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_signed_capture_marks_the_order_paid_once_and_a_redelivery_is_recognised()
    {
        var checkout = await OrderAsync();
        var json = PaymentEvent("payment.captured", checkout.OrderId, "pay_1", "captured");

        Assert.Equal(RazorpayWebhookOutcome.Applied, await DeliverAsync(json, "evt_1"));
        Assert.Equal(RazorpayWebhookOutcome.Duplicate, await DeliverAsync(json, "evt_1"));

        var order = await _db.RazorpayOrders.SingleAsync();
        Assert.Equal(RazorpayOrderStatus.Paid, order.Status);
        Assert.Equal("pay_1", order.PaymentId);
        Assert.Equal("netbanking", order.Method);
        Assert.Equal("payment.captured", order.LastWebhookEvent);

        var stored = await _db.RazorpayWebhookEvents.SingleAsync();
        Assert.True(stored.Applied);
        Assert.Equal(checkout.OrderId, stored.OrderId);
    }

    [Fact]
    public async Task A_late_failure_never_undoes_a_capture()
    {
        var checkout = await OrderAsync();
        await DeliverAsync(PaymentEvent("payment.captured", checkout.OrderId, "pay_2", "captured"), "evt_1");

        var outcome = await DeliverAsync(PaymentEvent("payment.failed", checkout.OrderId, "pay_1", "failed", "Card declined"), "evt_2");

        Assert.Equal(RazorpayWebhookOutcome.Ignored, outcome);
        Assert.Equal(RazorpayOrderStatus.Paid, (await _db.RazorpayOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_refund_webhook_brings_the_refunded_amount_up_to_date()
    {
        var checkout = await OrderAsync();
        await DeliverAsync(PaymentEvent("payment.captured", checkout.OrderId, "pay_1", "captured"), "evt_1");

        var refund = "{\"event\":\"refund.processed\",\"payload\":{\"refund\":{\"entity\":{\"id\":\"rfnd_1\",\"payment_id\":\"pay_1\",\"amount\":49950}}," +
                     "\"payment\":{\"entity\":{\"id\":\"pay_1\",\"order_id\":\"" + checkout.OrderId + "\",\"amount\":49950,\"amount_refunded\":49950,\"status\":\"refunded\"}}}}";

        Assert.Equal(RazorpayWebhookOutcome.Applied, await DeliverAsync(refund, "evt_2"));

        var order = await _db.RazorpayOrders.SingleAsync();
        Assert.Equal(RazorpayOrderStatus.Refunded, order.Status);
        Assert.Equal(49950, order.RefundedMinor);
        Assert.Equal("rfnd_1", order.LastRefundId);
    }

    [Fact]
    public async Task An_event_about_someone_elses_order_is_kept_but_changes_nothing()
    {
        Configure();

        Assert.Equal(RazorpayWebhookOutcome.Ignored, await DeliverAsync(PaymentEvent("payment.captured", "order_UNKNOWN", "pay_9", "captured"), "evt_9"));

        var stored = await _db.RazorpayWebhookEvents.SingleAsync();
        Assert.False(stored.Applied);
        Assert.Contains("Not one of", stored.Note);
    }

    [Fact]
    public async Task Without_an_event_id_the_body_itself_identifies_a_redelivery()
    {
        var checkout = await OrderAsync();
        var json = PaymentEvent("payment.authorized", checkout.OrderId, "pay_1", "authorized");
        var body = Body(json);
        var signature = RazorpaySignatures.Compute(WebhookSecret, body);

        Assert.Equal(RazorpayWebhookOutcome.Applied, await _service.HandleWebhookAsync(body, signature, null));
        Assert.Equal(RazorpayWebhookOutcome.Duplicate, await _service.HandleWebhookAsync(body, signature, null));
        Assert.Equal(RazorpayOrderStatus.Authorized, (await _db.RazorpayOrders.SingleAsync()).Status);
    }

    // ── Test doubles ────────────────────────────────────────────────────────────────────

    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeGateway : IRazorpayGateway
    {
        private int _orders;
        public string? PingError { get; set; }
        public RazorpayCredentials? LastCredentials { get; private set; }
        public long LastOrderAmount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastNotes { get; private set; }
        public Dictionary<string, RazorpayPaymentInfo> Payments { get; } = new();
        public int FetchCalls { get; private set; }
        public int CaptureCalls { get; private set; }
        public long? LastRefundAmount { get; private set; }

        public Task<RazorpayResult<bool>> PingAsync(RazorpayCredentials credentials, CancellationToken cancellationToken = default)
        {
            LastCredentials = credentials;
            return Task.FromResult(PingError is null ? RazorpayResult<bool>.Ok(true) : RazorpayResult<bool>.Fail(PingError));
        }

        public Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
            RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default)
        {
            LastOrderAmount = amountMinor;
            LastNotes = notes;
            return Task.FromResult(RazorpayResult<RazorpayOrderInfo>.Ok(new RazorpayOrderInfo($"order_T{++_orders:D6}", amountMinor, currency, receipt, "created")));
        }

        public Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            return Task.FromResult(Payments.TryGetValue(paymentId, out var p) ? RazorpayResult<RazorpayPaymentInfo>.Ok(p) : RazorpayResult<RazorpayPaymentInfo>.Fail("The id provided does not exist"));
        }

        public Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
            RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            var captured = Payments[paymentId] with { Status = "captured" };
            Payments[paymentId] = captured;
            return Task.FromResult(RazorpayResult<RazorpayPaymentInfo>.Ok(captured));
        }

        public Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default)
        {
            LastRefundAmount = amountMinor;
            var payment = Payments[paymentId];
            var amount = amountMinor ?? payment.Amount - payment.AmountRefunded;
            Payments[paymentId] = payment with { AmountRefunded = payment.AmountRefunded + amount };
            return Task.FromResult(RazorpayResult<RazorpayRefundInfo>.Ok(new RazorpayRefundInfo($"rfnd_{Guid.NewGuid():N}"[..14], paymentId, amount, "processed")));
        }
    }
}

/// <summary>The HTTP side: basic auth per request, Razorpay's snake_case bodies, and its error description surfaced as is.</summary>
public class RazorpayGatewayTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }

    private static readonly RazorpayCredentials Keys = new("rzp_test_1DP5mmOlF5G5ag", "secret");

    private static RazorpayGateway Gateway(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.razorpay.test/v1/") }, NullLogger<RazorpayGateway>.Instance);

    [Fact]
    public async Task Creating_an_order_posts_the_amount_in_paise_with_basic_auth()
    {
        var handler = new StubHandler { Body = "{\"id\":\"order_ABC\",\"amount\":49950,\"currency\":\"INR\",\"receipt\":\"r1\",\"status\":\"created\"}" };

        var result = await Gateway(handler).CreateOrderAsync(Keys, 49950, "INR", "r1", new Dictionary<string, string> { ["source"] = "test" });

        Assert.True(result.Success);
        Assert.Equal("order_ABC", result.Value!.Id);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.razorpay.test/v1/orders", handler.Request.RequestUri!.ToString());
        Assert.Equal("Basic", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("rzp_test_1DP5mmOlF5G5ag:secret")), handler.Request.Headers.Authorization.Parameter);
        Assert.Contains("\"amount\":49950", handler.RequestBody);
        Assert.Contains("\"receipt\":\"r1\"", handler.RequestBody);
        Assert.Contains("\"notes\":{\"source\":\"test\"}", handler.RequestBody);
    }

    [Fact]
    public async Task A_payment_is_read_from_razorpays_snake_case()
    {
        var handler = new StubHandler { Body = "{\"id\":\"pay_1\",\"order_id\":\"order_ABC\",\"amount\":49950,\"currency\":\"INR\",\"status\":\"captured\",\"method\":\"upi\",\"amount_refunded\":100,\"error_description\":null}" };

        var result = await Gateway(handler).FetchPaymentAsync(Keys, "pay_1");

        Assert.Equal(new RazorpayPaymentInfo("pay_1", "order_ABC", 49950, "INR", "captured", "upi", 100, null), result.Value);
        Assert.Equal("https://api.razorpay.test/v1/payments/pay_1", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Razorpays_error_description_is_what_comes_back()
    {
        var handler = new StubHandler
        {
            Status = HttpStatusCode.Unauthorized,
            Body = "{\"error\":{\"code\":\"BAD_REQUEST_ERROR\",\"description\":\"Authentication failed\"}}"
        };

        var result = await Gateway(handler).PingAsync(Keys);

        Assert.False(result.Success);
        Assert.Equal("Authentication failed", result.Error);
    }

    [Fact]
    public async Task A_partial_refund_sends_the_amount_and_a_full_one_sends_none()
    {
        var handler = new StubHandler { Body = "{\"id\":\"rfnd_1\",\"payment_id\":\"pay_1\",\"amount\":4000,\"status\":\"processed\"}" };
        var gateway = Gateway(handler);

        var partial = await gateway.RefundAsync(Keys, "pay_1", 4000);
        Assert.Equal("{\"amount\":4000}", handler.RequestBody);
        Assert.Equal(4000, partial.Value!.Amount);

        await gateway.RefundAsync(Keys, "pay_1", null);
        Assert.Equal("{}", handler.RequestBody);
        Assert.Equal("https://api.razorpay.test/v1/payments/pay_1/refund", handler.Request!.RequestUri!.ToString());
    }
}
