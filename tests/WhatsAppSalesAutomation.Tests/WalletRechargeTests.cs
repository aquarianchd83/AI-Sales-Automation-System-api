using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Application.Wallet;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Buying wallet credits with Razorpay: the server prices and validates the order, credits only after it has proved the payment, and credits exactly
/// once however many times the browser, the webhook or the reconciliation job report it.
/// </summary>
public sealed class WalletRechargeTests : IDisposable
{
    private const string KeyId = "rzp_test_1DP5mmOlF5G5ag";
    private const string KeySecret = "key-secret-123";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly MemorySettingsStore _store = new();
    private readonly WalletGateway _gateway = new();
    private readonly TestClock _clock = new();
    private readonly QuotaLedgerService _ledger;
    private readonly WalletRechargeService _service;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    public WalletRechargeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        var wallet = new CreditWalletService(_db, _ledger, new FeatureAccessService(_db), new CreditCostService(_db), _clock);
        var pricing = new PricingService(Snapshot.Of(new TaxOptions()), Snapshot.Of(new FxOptions()));
        _service = new WalletRechargeService(
            _db, _store, _gateway, pricing, _ledger, wallet, _clock, Snapshot.Of(new WalletRechargeOptions()), NullLogger<WalletRechargeService>.Instance);

        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Acme", Slug = "acme", CountryCode = "IN", StateCode = "MH" });
        _db.Tenants.Add(new Tenant { Id = _other, Name = "Globex", Slug = "globex", CountryCode = "IN", StateCode = "KA" });
        _db.SaveChanges();

        _store.Rows[RazorpayService.KeyIdKey] = KeyId;
        _store.Rows[RazorpayService.KeySecretKey] = KeySecret;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<RechargeCheckoutDto> OrderAsync(decimal amount = 500m, Guid? tenant = null) =>
        _service.CreateOrderAsync(tenant ?? _tenant, Guid.NewGuid(), new CreateRechargeOrderRequest(amount, "INR"));

    private static VerifyRechargePaymentRequest Signed(string orderId, string paymentId) =>
        new(orderId, paymentId, RazorpaySignatures.PaymentSignature(orderId, paymentId, KeySecret));

    /// <summary>Plays Razorpay: this payment exists on the order, in this state, for this many paise.</summary>
    private RazorpayPaymentInfo Pay(RechargeCheckoutDto order, string paymentId, string status = "captured", long? amountMinor = null, string currency = "INR") =>
        _gateway.Payments[paymentId] = new RazorpayPaymentInfo(paymentId, order.OrderId, amountMinor ?? order.AmountMinor, currency, status, "upi", 0, status == "failed" ? "Bank declined" : null);

    private async Task<decimal> BalanceAsync(Guid? tenant = null) => (await _service.GetBalanceAsync(tenant ?? _tenant)).Balance;

    private Task<int> LedgerEntriesAsync() =>
        _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.TenantId == _tenant && e.EntryType == QuotaEntryType.Purchase);

    private static WalletWebhookEvent Hook(string name, RechargeCheckoutDto order, string? paymentId, long? refunded = null, long? refundAmount = null, string? refundId = null, long? amount = null) =>
        new(name, order.OrderId, paymentId, "upi", null, amount ?? order.AmountMinor, "INR", name == "payment.failed" ? "failed" : "captured", refundId, refundAmount, refunded);

    // ── Creating the order ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_order_is_priced_on_the_server_with_gst_on_top_and_credits_for_the_amount_before_tax()
    {
        var order = await OrderAsync(500m);

        Assert.Equal(500m, order.Amount);
        Assert.Equal(90m, order.Tax);            // 18% GST, the platform's default rule
        Assert.Equal(590m, order.Total);
        Assert.Equal(59_000, order.AmountMinor); // what Razorpay is asked to collect, in paise
        Assert.Equal(500m, order.Credits);       // 1 credit per rupee, before tax
        Assert.Equal(KeyId, order.KeyId);
        Assert.StartsWith("order_", order.OrderId);
        Assert.Equal(59_000, _gateway.LastOrderAmount);

        var row = await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_tenant, row.TenantId);
        Assert.Equal(PaymentTransactionStatus.Created, row.Status);
        Assert.Equal(0m, await BalanceAsync());  // nothing is credited by creating an order
    }

    [Theory]
    [InlineData(99)]
    [InlineData(50_001)]
    [InlineData(0)]
    [InlineData(-500)]
    public async Task An_amount_outside_the_limits_is_refused_before_razorpay_is_asked(int amount)
    {
        await Assert.ThrowsAsync<ValidationException>(() => OrderAsync(amount));
        Assert.Equal(0, _gateway.OrdersCreated);
    }

    [Fact]
    public async Task Fractions_of_a_paisa_and_other_currencies_are_refused()
    {
        await Assert.ThrowsAsync<ValidationException>(() => OrderAsync(500.555m));
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateOrderAsync(_tenant, null, new CreateRechargeOrderRequest(500m, "USD")));
    }

    [Fact]
    public async Task Without_razorpay_keys_no_order_is_made()
    {
        _store.Rows.Clear();
        await Assert.ThrowsAsync<ConflictException>(() => OrderAsync());
    }

    [Fact]
    public async Task A_tenant_billed_outside_india_cannot_recharge_through_razorpay()
    {
        var abroad = Guid.NewGuid();
        _db.Tenants.Add(new Tenant { Id = abroad, Name = "Abroad", Slug = "abroad", CountryCode = "DE" });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => OrderAsync(500m, abroad));
    }

    // ── Verifying what the browser reports ──────────────────────────────────────────────

    [Fact]
    public async Task A_verified_captured_payment_credits_the_wallet_with_a_ledger_entry_and_a_payment_row()
    {
        await _ledger.GrantPlanCreditsAsync(_tenant, 1000m, "seed", "seed", QuotaGrantOrigin.PlanAllocation);
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");

        var result = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.True(result.Success);
        Assert.Equal(1500m, await BalanceAsync());                // ₹1,000 + ₹500
        Assert.Equal(1500m, result.Balance);
        Assert.Equal("Credited", result.Transaction!.Status);
        Assert.Equal(1000m, result.Transaction.BalanceBefore);
        Assert.Equal(1500m, result.Transaction.BalanceAfter);

        var entry = await _db.QuotaLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.EntryType == QuotaEntryType.Purchase);
        Assert.Equal(500m, entry.UnitsDelta);
        Assert.Equal(1500m, entry.BalanceAfter);
        Assert.Equal(_tenant, entry.TenantId);
        Assert.Contains("RAZORPAY", entry.Note);
        Assert.Contains("pay_A1", entry.Note);

        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentKind.WalletTopUp, payment.Kind);
        Assert.Equal("Razorpay", payment.Provider);
        Assert.Equal("pay_A1", payment.ProviderPaymentId);
        Assert.Equal(590m, payment.TotalLocal);
        Assert.Equal(90m, payment.TaxLocal);
    }

    [Fact]
    public async Task Verifying_the_same_payment_again_does_not_credit_twice()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");

        await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));
        var again = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.True(again.Success);
        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(1, await LedgerEntriesAsync());
        Assert.Equal(1, await _db.Payments.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_second_payment_on_an_already_paid_order_is_not_credited()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");
        Pay(order, "pay_A2");

        await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));
        var second = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A2"));

        Assert.False(second.Success);
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task A_bad_signature_credits_nothing_and_does_not_even_ask_razorpay_about_the_payment()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");

        var result = await _service.VerifyAsync(_tenant, new VerifyRechargePaymentRequest(order.OrderId, "pay_A1", "deadbeef"));

        Assert.False(result.Success);
        Assert.False(result.Pending);
        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal(0, _gateway.FetchCalls);
    }

    [Fact]
    public async Task A_signature_made_for_another_payment_id_is_refused()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");

        var forged = new VerifyRechargePaymentRequest(order.OrderId, "pay_A1", RazorpaySignatures.PaymentSignature(order.OrderId, "pay_OTHER", KeySecret));
        Assert.False((await _service.VerifyAsync(_tenant, forged)).Success);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task A_payment_for_a_different_amount_is_rejected_and_credits_nothing()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1", amountMinor: 100); // paid ₹1.00 against a ₹590 order

        var result = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.False(result.Success);
        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal("Failed", result.Transaction!.Status);
    }

    [Fact]
    public async Task A_payment_in_another_currency_is_rejected()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1", currency: "USD");

        Assert.False((await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"))).Success);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task A_payment_that_belongs_to_another_order_is_rejected()
    {
        var order = await OrderAsync(500m);
        var other = await OrderAsync(500m);
        _gateway.Payments["pay_X"] = new RazorpayPaymentInfo("pay_X", other.OrderId, order.AmountMinor, "INR", "captured", "upi", 0, null);

        Assert.False((await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_X"))).Success);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task An_authorized_payment_is_captured_by_the_server_before_crediting()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1", "authorized");

        var result = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.True(result.Success);
        Assert.Equal(1, _gateway.CaptureCalls);
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task A_failed_payment_credits_nothing()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1", "failed");

        var result = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.False(result.Success);
        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal("Failed", result.Transaction!.Status);
        Assert.Equal("Bank declined", result.Transaction.FailureReason);
    }

    [Fact]
    public async Task When_razorpays_record_cannot_be_read_the_result_is_pending_not_a_failure_and_nothing_is_credited()
    {
        var order = await OrderAsync(500m); // pay_A1 is not known to the gateway

        var result = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.False(result.Success);
        Assert.True(result.Pending);
        Assert.Equal(0m, await BalanceAsync());
    }

    // ── Webhooks: browser closed, duplicates, delays ────────────────────────────────────

    [Fact]
    public async Task A_captured_webhook_credits_the_wallet_even_though_the_browser_never_came_back()
    {
        var order = await OrderAsync(500m);

        var result = await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1"));

        Assert.True(result.Matched);
        Assert.True(result.Applied);
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task A_duplicate_webhook_and_a_later_browser_verify_never_credit_twice()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");

        await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1"));
        var duplicate = await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1"));
        var paidEvent = await _service.HandleWebhookAsync(Hook("order.paid", order, "pay_A1"));
        var verify = await _service.VerifyAsync(_tenant, Signed(order.OrderId, "pay_A1"));

        Assert.False(duplicate.Applied);
        Assert.False(paidEvent.Applied);
        Assert.True(verify.Success);
        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(1, await LedgerEntriesAsync());
        Assert.Equal(1, await _db.Payments.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_webhook_amount_that_does_not_match_the_order_is_not_credited()
    {
        var order = await OrderAsync(500m);

        var result = await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1", amount: 100));

        Assert.False(result.Applied);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task A_failed_webhook_credits_nothing_and_a_later_successful_retry_on_the_same_order_still_does()
    {
        var order = await OrderAsync(500m);

        await _service.HandleWebhookAsync(Hook("payment.failed", order, "pay_F1"));
        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal(PaymentTransactionStatus.Failed, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Status);

        await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1"));
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task A_late_failed_webhook_cannot_undo_a_credited_payment()
    {
        var order = await OrderAsync(500m);
        await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1"));

        await _service.HandleWebhookAsync(Hook("payment.failed", order, "pay_F1"));

        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(PaymentTransactionStatus.Credited, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task An_authorized_webhook_credits_nothing_until_the_payment_is_captured()
    {
        var order = await OrderAsync(500m);

        await _service.HandleWebhookAsync(Hook("payment.authorized", order, "pay_A1"));

        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal(PaymentTransactionStatus.Authorized, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_webhook_for_an_order_that_is_not_a_wallet_top_up_is_left_alone()
    {
        var result = await _service.HandleWebhookAsync(new WalletWebhookEvent("payment.captured", "order_SOMEONE_ELSES", "pay_Z", "upi", null, 100, "INR", "captured", null, null, null));

        Assert.False(result.Matched);
    }

    // ── Refunds: compensating entries, never edits ──────────────────────────────────────

    [Fact]
    public async Task A_refund_takes_the_refunded_share_of_credits_back_with_a_new_ledger_entry_and_only_once()
    {
        var order = await OrderAsync(500m);
        await _service.HandleWebhookAsync(Hook("payment.captured", order, "pay_A1"));
        var original = await _db.QuotaLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.EntryType == QuotaEntryType.Purchase);

        // Half of the ₹590 payment is refunded: half the credits come back out.
        await _service.HandleWebhookAsync(Hook("refund.created", order, "pay_A1", refunded: 29_500, refundAmount: 29_500, refundId: "rfnd_1"));
        await _service.HandleWebhookAsync(Hook("refund.processed", order, "pay_A1", refunded: 29_500, refundAmount: 29_500, refundId: "rfnd_1"));

        Assert.Equal(250m, await BalanceAsync());
        var entries = await _db.QuotaLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == _tenant).OrderBy(e => e.OccurredAtUtc).ToListAsync();
        Assert.Equal(1, entries.Count(e => e.EntryType == QuotaEntryType.RefundClawback));
        Assert.Equal(-250m, entries.Single(e => e.EntryType == QuotaEntryType.RefundClawback).UnitsDelta);

        // The original credit entry is untouched.
        var after = await _db.QuotaLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(original.UnitsDelta, after.UnitsDelta);
        Assert.Equal(original.BalanceAfter, after.BalanceAfter);
        Assert.Equal(PaymentTransactionStatus.PartiallyRefunded, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Status);
    }

    // ── Reconciliation ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_credits_a_payment_whose_webhook_and_browser_callback_were_both_lost()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);

        var changed = await _service.ReconcilePendingAsync();

        Assert.Equal(1, changed);
        Assert.Equal(500m, await BalanceAsync());

        _clock.UtcNow = _clock.UtcNow.AddMinutes(30);
        Assert.Equal(0, await _service.ReconcilePendingAsync());
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task Reconciliation_leaves_a_fresh_order_alone_and_marks_an_abandoned_checkout_expired()
    {
        await OrderAsync(500m);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        Assert.Equal(0, await _service.ReconcilePendingAsync());   // still within the checkout window
        Assert.Equal(PaymentTransactionStatus.Created, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Status);

        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        Assert.Equal(1, await _service.ReconcilePendingAsync());
        Assert.Equal(PaymentTransactionStatus.Expired, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task Asking_for_the_status_of_a_paid_but_unreported_top_up_settles_it()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");
        var id = (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Id;

        var status = await _service.GetStatusAsync(_tenant, id);

        Assert.Equal("Credited", status.Status);
        Assert.Equal(500m, await BalanceAsync());
    }

    // ── Tenant isolation and history ────────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenant_cannot_verify_read_or_list_a_tenants_transactions()
    {
        var order = await OrderAsync(500m);
        Pay(order, "pay_A1");
        var id = (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).Id;

        await Assert.ThrowsAsync<NotFoundException>(() => _service.VerifyAsync(_other, Signed(order.OrderId, "pay_A1")));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetStatusAsync(_other, id));
        Assert.Empty((await _service.GetHistoryAsync(_other, new PagedRequest())).Items);
        Assert.Equal(0m, await BalanceAsync(_other));
        Assert.Equal(0m, await BalanceAsync());          // and the failed attempt credited nobody
        Assert.Single((await _service.GetHistoryAsync(_tenant, new PagedRequest())).Items);
    }

    [Fact]
    public async Task Credits_land_in_the_tenant_that_made_the_order()
    {
        var mine = await OrderAsync(500m);
        var theirs = await OrderAsync(1000m, _other);

        await _service.HandleWebhookAsync(Hook("payment.captured", theirs, "pay_B1"));

        Assert.Equal(1000m, await BalanceAsync(_other));
        Assert.Equal(0m, await BalanceAsync());
        await _service.HandleWebhookAsync(Hook("payment.captured", mine, "pay_A1"));
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task History_lists_the_newest_first_with_status_and_amounts()
    {
        var first = await OrderAsync(100m);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        var second = await OrderAsync(200m);
        await _service.HandleWebhookAsync(Hook("payment.captured", second, "pay_A2"));

        var history = await _service.GetHistoryAsync(_tenant, new PagedRequest());

        Assert.Equal(2, history.TotalCount);
        Assert.Equal(new[] { second.OrderId, first.OrderId }, history.Items.Select(i => i.RazorpayOrderId));
        Assert.Equal(new[] { "Credited", "Created" }, history.Items.Select(i => i.Status));
        Assert.Equal(new[] { 236m, 118m }, history.Items.Select(i => i.Total));
    }

    [Fact]
    public async Task The_credits_per_rupee_rate_is_applied_and_remembered_on_the_transaction()
    {
        var wallet = new CreditWalletService(_db, _ledger, new FeatureAccessService(_db), new CreditCostService(_db), _clock);
        var service = new WalletRechargeService(
            _db, _store, _gateway, new PricingService(Snapshot.Of(new TaxOptions()), Snapshot.Of(new FxOptions())), _ledger, wallet, _clock,
            Snapshot.Of(new WalletRechargeOptions { CreditsPerRupee = 2.5m }), NullLogger<WalletRechargeService>.Instance);

        var order = await service.CreateOrderAsync(_tenant, null, new CreateRechargeOrderRequest(100m, "INR"));

        Assert.Equal(250m, order.Credits);
        Assert.Equal(2.5m, (await _db.PaymentTransactions.IgnoreQueryFilters().SingleAsync()).CreditsPerRupee);
    }

    // ── Razorpay, played by the test ────────────────────────────────────────────────────

    private sealed class WalletGateway : IRazorpayGateway
    {
        public Dictionary<string, RazorpayPaymentInfo> Payments { get; } = new();
        public int OrdersCreated { get; private set; }
        public long LastOrderAmount { get; private set; }
        public int FetchCalls { get; private set; }
        public int CaptureCalls { get; private set; }

        public Task<RazorpayResult<bool>> PingAsync(RazorpayCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.FromResult(RazorpayResult<bool>.Ok(true));

        public Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
            RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default)
        {
            LastOrderAmount = amountMinor;
            return Task.FromResult(RazorpayResult<RazorpayOrderInfo>.Ok(new RazorpayOrderInfo($"order_W{++OrdersCreated:D6}", amountMinor, currency, receipt, "created")));
        }

        public Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            return Task.FromResult(Payments.TryGetValue(paymentId, out var p) ? RazorpayResult<RazorpayPaymentInfo>.Ok(p) : RazorpayResult<RazorpayPaymentInfo>.Fail("The id provided does not exist"));
        }

        public Task<RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>> FetchOrderPaymentsAsync(RazorpayCredentials credentials, string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RazorpayResult<IReadOnlyList<RazorpayPaymentInfo>>.Ok(Payments.Values.Where(p => p.OrderId == orderId).ToList()));

        public Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
            RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            var captured = Payments[paymentId] with { Status = "captured" };
            Payments[paymentId] = captured;
            return Task.FromResult(RazorpayResult<RazorpayPaymentInfo>.Ok(captured));
        }

        public Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RazorpayResult<RazorpayPlanInfo>> CreatePlanAsync(
            RazorpayCredentials credentials, string period, int interval, string name, long amountMinor, string currency, IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RazorpayResult<RazorpaySubscriptionInfo>> CreateSubscriptionAsync(
            RazorpayCredentials credentials, string planId, int totalCount, bool customerNotify, long? expireByUnix, IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RazorpayResult<RazorpaySubscriptionInfo>> FetchSubscriptionAsync(RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RazorpayResult<RazorpaySubscriptionInfo>> CancelSubscriptionAsync(RazorpayCredentials credentials, string subscriptionId, bool cancelAtCycleEnd, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>> FetchSubscriptionInvoicesAsync(RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RazorpayResult<RazorpayInvoiceInfo>> FetchInvoiceAsync(RazorpayCredentials credentials, string invoiceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
