using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Billing.AutoPay;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// What an AutoPay payment gives a tenant, against the real wallet ledger: the payment record, the plan and its paid-through date, and the
/// credits - each exactly once, whatever is repeated, raced or retried. Premium costs Rs 2,407 + 18% GST = Rs 2,840.26 a month and puts
/// 100 credits in the wallet.
/// </summary>
public sealed class AutoPayEntitlementsTests : IDisposable
{
    private const string KeyId = "rzp_test_1DP5mmOlF5G5ag";
    private const string Sub = "sub_T000001";
    private const long Paise = 284026;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly MemorySettingsStore _store = new();
    private readonly FakeSubscriptionGateway _gateway = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly QuotaLedgerService _ledger;
    private readonly CreditAllocationService _credits;
    private readonly AutoPayEntitlements _entitlements;
    private readonly RazorpaySubscriptionEventProcessor _processor;
    private readonly AutoPayMaintenanceService _maintenance;
    private readonly List<Guid> _jobSyncs = new();
    private readonly ITenantJobProvisioner _jobs;

    private readonly Tenant _tenant = new() { Name = "Mumbai Co", Slug = "mumbai", CountryCode = "IN", StateCode = "MH", Status = TenantStatus.Trial, TrialEndsAtUtc = new DateTime(2026, 9, 20) };
    private readonly Tenant _other = new() { Name = "Delhi Co", Slug = "delhi", CountryCode = "IN", StateCode = "DL", Status = TenantStatus.Active };
    private readonly Plan _trial = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Trial", StarterCredits = 50m, IsActive = true };
    private readonly Plan _premium = new() { Code = PlatformFeatureCatalog.PremiumPlanCode, Name = "Premium", PriceMonthlyCents = 2900, MonthlyCredits = 100m, IsActive = true };
    private readonly RazorpayPlan _razorpayPlan;
    private readonly long _t0;
    private int _eventNumber;

    public AutoPayEntitlementsTests()
    {
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(_dbOptions, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _store.Rows[RazorpayKeyStore.LegacyKeyIdKey] = KeyId;
        _store.Rows[RazorpayKeyStore.LegacyKeySecretKey] = "key-secret-123";

        _ledger = new QuotaLedgerService(_db, _clock);
        _credits = new CreditAllocationService(_db, _ledger, _clock);
        _jobs = Fake.Of<ITenantJobProvisioner>((m, args) =>
        {
            if (m.Name != nameof(ITenantJobProvisioner.SyncTenantAsync))
                throw new NotImplementedException(m.Name);
            _jobSyncs.Add((Guid)args![0]!);
            return Task.CompletedTask;
        });
        _entitlements = Entitlements(_ledger);
        _processor = new RazorpaySubscriptionEventProcessor(_db, _store, _gateway, _entitlements, _clock, NullLogger<RazorpaySubscriptionEventProcessor>.Instance);
        _maintenance = new AutoPayMaintenanceService(
            _db, _store, _gateway, _processor, _entitlements, _clock, new FixedOptions<AutoPayOptions>(new()), NullLogger<AutoPayMaintenanceService>.Instance);

        _razorpayPlan = new RazorpayPlan { PlanId = _premium.Id, Mode = "test", AmountMinor = Paise, RazorpayPlanId = "plan_T000001" };
        _db.Tenants.AddRange(_tenant, _other);
        _db.Plans.AddRange(_trial, _premium);
        _db.PlanPrices.Add(new PlanPrice { PlanId = _premium.Id, CountryCode = "IN", Amount = 2407m });
        _db.PlanPrices.Add(new PlanPrice { PlanId = _trial.Id, CountryCode = "IN", Amount = 0m });
        _db.RazorpayPlans.Add(_razorpayPlan);
        _db.SaveChanges();

        _t0 = Unix(_clock.UtcNow);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Building blocks ─────────────────────────────────────────────────────────────────

    private AutoPayEntitlements Entitlements(IQuotaLedgerService ledger) =>
        new(_db, TestPricing.Default(), ledger, _credits, _notifier, _jobs, _clock, NullLogger<AutoPayEntitlements>.Instance);

    private static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>A mandate for the tenant, running, in its first cycle: paid from "now" for a month.</summary>
    private async Task<RazorpaySubscription> MandateAsync(
        string id = Sub, RazorpaySubscriptionStatus status = RazorpaySubscriptionStatus.Active, Tenant? tenant = null, int cycle = 0)
    {
        var start = _clock.UtcNow.AddMonths(cycle);
        var row = new RazorpaySubscription
        {
            TenantId = (tenant ?? _tenant).Id, PlanId = _premium.Id, RazorpayPlanRowId = _razorpayPlan.Id, RazorpaySubscriptionId = id, Mode = "test",
            Status = status, AmountMinor = Paise, TotalCount = 120, PaymentMethod = "upi", CreatedAt = _clock.UtcNow.AddHours(-1),
            CurrentStartUtc = start, CurrentEndUtc = start.AddMonths(1)
        };
        _db.RazorpaySubscriptions.Add(row);
        await _db.SaveChangesAsync();
        _gateway.Subscriptions[id] = new RazorpaySubscriptionInfo(
            id, "plan_T000001", status.ToString().ToLowerInvariant(), null, 120, 0, 120, Unix(start), Unix(start.AddMonths(1)), Unix(start.AddMonths(1)), null, null, null, "cust_1", "upi");
        return row;
    }

    private RazorpayWebhookPayment Pay(string id = "pay_1", long amount = Paise, string status = "captured", string? error = null, int cycle = -1) => new(
        id, amount, "INR", status, "upi", "inv_" + id[4..], "order_1", error, _clock.UtcNow.AddMinutes(5),
        cycle < 0 ? null : _clock.UtcNow.AddMonths(cycle), cycle < 0 ? null : _clock.UtcNow.AddMonths(cycle + 1));

    private Task<RazorpaySubscription> ReloadAsync(string id = Sub) =>
        _db.RazorpaySubscriptions.IgnoreQueryFilters().SingleAsync(s => s.RazorpaySubscriptionId == id);

    private async Task<decimal> BalanceAsync(Tenant? tenant = null) =>
        (await _ledger.GetBalancesAsync((tenant ?? _tenant).Id)).Where(b => b.QuotaType == QuotaType.Credits).Sum(b => b.Balance);

    private Task<List<Payment>> PaymentsAsync() =>
        _db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == _tenant.Id).OrderBy(p => p.ProviderPaymentId).ToListAsync();

    private Task<Subscription> SubscriptionAsync(Tenant? tenant = null) =>
        _db.Subscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.TenantId == (tenant ?? _tenant).Id);

    private Task<Tenant> TenantAsync() => _db.Tenants.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == _tenant.Id);

    private Task<int> ChargeGrantsAsync() =>
        _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.TenantId == _tenant.Id && e.OperationKey.StartsWith("rzp-charge:"));

    private string ChargedBody(string paymentId, int at, int paid = 1)
    {
        var subscription = new
        {
            entity = new
            {
                id = Sub, plan_id = "plan_T000001", status = "active", current_start = _t0, current_end = _t0 + 2_592_000, charge_at = _t0 + 2_592_000,
                total_count = 120, paid_count = paid, remaining_count = 120 - paid, customer_id = "cust_1", payment_method = "upi",
                notes = new Dictionary<string, string> { ["source"] = "autopay" }
            }
        };
        var payment = new
        {
            entity = new { id = paymentId, amount = Paise, currency = "INR", status = "captured", method = "upi", invoice_id = "inv_1", order_id = "order_1", created_at = _t0 + at }
        };
        return JsonSerializer.Serialize(new { @event = "subscription.charged", payload = new { subscription, payment }, created_at = _t0 + at });
    }

    private Guid Stored(string body, string name = "subscription.charged")
    {
        var record = new RazorpayWebhookEvent { EventId = $"evt_{++_eventNumber}", Event = name, SubscriptionId = Sub, Payload = body, CreatedAt = _clock.UtcNow.AddMinutes(-10) };
        _db.RazorpayWebhookEvents.Add(record);
        _db.SaveChanges();
        return record.Id;
    }

    // ── One payment ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_payment_is_recorded_with_the_gateways_references_and_the_tax_it_included()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        var payment = Assert.Single(await PaymentsAsync());
        Assert.Equal(("Razorpay", "pay_1", "inv_1", Sub, "upi"), (payment.Provider, payment.ProviderPaymentId, payment.ProviderInvoiceId, payment.ProviderSubscriptionId, payment.PaymentMethod));
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(PaymentKind.Subscription, payment.Kind);
        Assert.Equal((_premium.Id, "Premium"), (payment.PlanId!.Value, payment.PlanName));
        Assert.Equal((2407m, 433.26m, 2840.26m, 2840.26m, "INR"), (payment.LocalAmount, payment.TaxLocal, payment.TotalLocal, payment.AmountInr, payment.CurrencyCode));
        var lines = PaymentDto.ParseTaxLines(payment.TaxLinesJson);
        Assert.Equal(new[] { ("CGST", 216.63m), ("SGST", 216.63m) }, lines.Select(l => (l.Name, l.Amount)).ToArray());
        Assert.Equal(_clock.UtcNow.AddMinutes(5), payment.PaidAtUtc);
        Assert.Equal((_clock.UtcNow, _clock.UtcNow.AddMonths(1)), (payment.PeriodStartUtc!.Value, payment.PeriodEndUtc!.Value));
    }

    [Fact]
    public async Task A_payment_puts_the_tenant_on_the_plan_paid_to_the_end_of_the_cycle()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        var subscription = await SubscriptionAsync();
        Assert.Equal((_premium.Id, SubscriptionStatus.Active, mandate.Id), (subscription.PlanId!.Value, subscription.Status, subscription.RazorpaySubscriptionRowId!.Value));
        Assert.Equal((_clock.UtcNow, _clock.UtcNow.AddMonths(1)), (subscription.CurrentPeriodStartUtc!.Value, subscription.CurrentPeriodEndUtc!.Value));
        Assert.False(subscription.CancelAtPeriodEnd);

        var tenant = await TenantAsync();
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Null(tenant.TrialEndsAtUtc);
        Assert.Equal(_tenant.Id, Assert.Single(_jobSyncs));
    }

    [Fact]
    public async Task A_payment_adds_the_plans_credits_through_the_wallet_ledger_linked_to_the_payment()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        Assert.Equal(100m, await BalanceAsync());
        var payment = Assert.Single(await PaymentsAsync());
        var entry = await _db.QuotaLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.TenantId == _tenant.Id);
        Assert.Equal((QuotaEntryType.Allocation, 100m, "rzp-charge:pay_1", "Payment", payment.Id.ToString()), (entry.EntryType, entry.UnitsDelta, entry.OperationKey, entry.ReferenceType, entry.ReferenceId));
        var grant = await _db.QuotaGrants.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.TenantId == _tenant.Id);
        Assert.Equal((QuotaGrantOrigin.PlanAllocation, payment.Id), (grant.Origin, grant.PaymentId!.Value));
    }

    [Fact]
    public async Task A_payment_tells_the_tenant_once()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal((_tenant.Id, TenantNotificationKind.AutoPayPaymentReceived, "pay_1"), (notice.TenantId, notice.Kind, notice.EpisodeKey));
        Assert.Contains("2,840.26", notice.Body);
        Assert.Contains("100 credits", notice.Body);
    }

    [Fact]
    public async Task What_the_tenant_already_had_in_the_wallet_is_untouched()
    {
        await _credits.AssignTrialPlanAsync(_tenant.Id);
        Assert.Equal(50m, await BalanceAsync());
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        Assert.Equal(150m, await BalanceAsync());
    }

    // ── Exactly once ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_payment_five_times_is_one_payment_and_one_lot_of_credits()
    {
        var mandate = await MandateAsync();

        for (var i = 0; i < 5; i++)
            await _entitlements.ChargeSucceededAsync(mandate, Pay());

        Assert.Single(await PaymentsAsync());
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(1, await ChargeGrantsAsync());
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task The_same_webhook_delivered_five_times_credits_once()
    {
        await MandateAsync(status: RazorpaySubscriptionStatus.Created);
        var id = Stored(ChargedBody("pay_1", 20));

        for (var i = 0; i < 5; i++)
            await _processor.ProcessAsync(id);

        Assert.Single(await PaymentsAsync());
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(RazorpayWebhookProcessingStatus.Completed, (await _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync()).ProcessingStatus);
    }

    [Fact]
    public async Task One_payment_reported_by_two_different_events_credits_once()
    {
        await MandateAsync(status: RazorpaySubscriptionStatus.Created);
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };

        // Two events, two event ids, one payment: de-duplicating webhooks alone would not catch this.
        Assert.Equal(RazorpayEventOutcome.Completed, await _processor.ProcessAsync(Stored(ChargedBody("pay_1", 20))));
        Assert.Equal(RazorpayEventOutcome.Completed, await _processor.ProcessAsync(Stored(ChargedBody("pay_1", 21))));

        Assert.Single(await PaymentsAsync());
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(1, await ChargeGrantsAsync());
    }

    [Fact]
    public async Task Two_workers_recording_the_same_payment_at_once_leave_one_payment_and_one_lot_of_credits()
    {
        var mandate = await MandateAsync();
        // The other worker gets its row in after our check and before our save.
        var raced = false;
        _db.BeforeSave = _ =>
        {
            if (raced)
                return;
            raced = true;
            using var other = new SqliteApplicationDbContext(_dbOptions, new PlatformContext(), new AnonymousUser());
            other.Payments.Add(new Payment
            {
                TenantId = _tenant.Id, PlanId = _premium.Id, PlanName = "Premium", CurrencyCode = "INR", CurrencySymbol = "₹", LocalAmount = 2407m,
                TotalLocal = 2840.26m, Provider = "Razorpay", ProviderPaymentId = "pay_1", ProviderSubscriptionId = Sub, PaidAtUtc = _clock.UtcNow
            });
            other.SaveChanges();
        };

        // Ours is refused by the database rather than written twice...
        await Assert.ThrowsAsync<DbUpdateException>(() => _entitlements.ChargeSucceededAsync(mandate, Pay()));
        _db.ChangeTracker.Clear();

        // ...and the retry finishes the job without adding a second anything.
        await _entitlements.ChargeSucceededAsync(await ReloadAsync(), Pay());

        Assert.Single(await PaymentsAsync());
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(1, await ChargeGrantsAsync());
        Assert.Equal(_premium.Id, (await SubscriptionAsync()).PlanId);
    }

    [Fact]
    public async Task If_the_wallet_fails_after_the_payment_is_saved_the_retry_adds_the_credits_and_no_second_payment()
    {
        var mandate = await MandateAsync();
        var fail = true;
        var flaky = Fake.Of<IQuotaLedgerService>((m, args) =>
        {
            if (fail && m.Name == nameof(IQuotaLedgerService.GrantPlanCreditsAsync) && ((string)args![2]!).StartsWith("rzp-charge:"))
            {
                fail = false;
                throw new InvalidOperationException("The wallet could not be reached.");
            }
            return m.Invoke(_ledger, args);
        });
        var entitlements = Entitlements(flaky);

        await Assert.ThrowsAsync<InvalidOperationException>(() => entitlements.ChargeSucceededAsync(mandate, Pay()));

        // The money is on record; the credits are owed.
        Assert.Single(await PaymentsAsync());
        Assert.Equal(0m, await BalanceAsync());
        Assert.Empty(_notifier.Sent);

        await entitlements.ChargeSucceededAsync(mandate, Pay());

        Assert.Single(await PaymentsAsync());
        Assert.Equal(100m, await BalanceAsync());
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task A_webhook_whose_credits_failed_is_kept_and_credited_on_the_retry_pass()
    {
        await MandateAsync(status: RazorpaySubscriptionStatus.Created);
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };
        var fail = true;
        var flaky = Fake.Of<IQuotaLedgerService>((m, args) =>
        {
            if (fail && m.Name == nameof(IQuotaLedgerService.GrantPlanCreditsAsync) && ((string)args![2]!).StartsWith("rzp-charge:"))
            {
                fail = false;
                throw new InvalidOperationException("The wallet could not be reached.");
            }
            return m.Invoke(_ledger, args);
        });
        var processor = new RazorpaySubscriptionEventProcessor(_db, _store, _gateway, Entitlements(flaky), _clock, NullLogger<RazorpaySubscriptionEventProcessor>.Instance);
        var maintenance = new AutoPayMaintenanceService(
            _db, _store, _gateway, processor, Entitlements(flaky), _clock, new FixedOptions<AutoPayOptions>(new()), NullLogger<AutoPayMaintenanceService>.Instance);
        var id = Stored(ChargedBody("pay_1", 20));

        Assert.Equal(RazorpayEventOutcome.Failed, await processor.ProcessAsync(id));
        Assert.Equal(0m, await BalanceAsync());

        _clock.UtcNow = _clock.UtcNow.AddMinutes(6);
        Assert.Equal(1, await maintenance.RetryWebhooksAsync());

        Assert.Equal(100m, await BalanceAsync());
        Assert.Single(await PaymentsAsync());
        Assert.Equal(RazorpayWebhookProcessingStatus.Completed, (await _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync()).ProcessingStatus);
    }

    // ── Month after month ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_months_payment_adds_its_own_credits_and_moves_the_paid_through_date()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_1", cycle: 0));
        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_2", cycle: 1));
        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_3", cycle: 2));

        Assert.Equal(3, (await PaymentsAsync()).Count);
        // Unspent credits carry over, as they always have.
        Assert.Equal(300m, await BalanceAsync());
        Assert.Equal(_clock.UtcNow.AddMonths(3), (await SubscriptionAsync()).CurrentPeriodEndUtc);
        Assert.Equal(3, _notifier.Sent.Count);
    }

    [Fact]
    public async Task A_payment_for_an_earlier_cycle_arriving_late_does_not_shorten_what_is_paid_for()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_2", cycle: 1));

        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_1", cycle: 0));

        Assert.Equal(_clock.UtcNow.AddMonths(2), (await SubscriptionAsync()).CurrentPeriodEndUtc);
        // It was still a payment, and it still earned its credits.
        Assert.Equal(200m, await BalanceAsync());
    }

    [Fact]
    public async Task The_calendar_month_job_no_longer_hands_credits_to_a_plan_a_mandate_pays_for()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay());
        // Another tenant on Premium with no mandate (an operator put them there): the job is still theirs.
        _db.Subscriptions.Add(new Subscription { TenantId = _other.Id, PlanId = _premium.Id, Status = SubscriptionStatus.Active });
        await _db.SaveChangesAsync();

        var granted = await _credits.AllocateMonthlyAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(1);
        granted += await _credits.AllocateMonthlyAsync();

        Assert.Equal(2, granted);
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(200m, await BalanceAsync(_other));
    }

    // ── What is not credited ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100L)]
    [InlineData(284025L)]
    [InlineData(500000L)]
    public async Task A_payment_for_a_different_amount_than_the_mandate_is_not_credited(long amount)
    {
        var mandate = await MandateAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _entitlements.ChargeSucceededAsync(mandate, Pay(amount: amount)));

        Assert.Contains("Nothing was credited", ex.Message);
        Assert.Empty(await PaymentsAsync());
        Assert.Equal(0m, await BalanceAsync());
        Assert.False(await _db.Subscriptions.IgnoreQueryFilters().AnyAsync());
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task A_payment_in_another_currency_is_not_credited()
    {
        var mandate = await MandateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _entitlements.ChargeSucceededAsync(mandate, Pay() with { Currency = "USD" }));

        Assert.Empty(await PaymentsAsync());
    }

    [Fact]
    public async Task One_tenants_payment_never_reaches_anothers_wallet()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        Assert.Equal(0m, await BalanceAsync(_other));
        Assert.False(await _db.Payments.IgnoreQueryFilters().AnyAsync(p => p.TenantId == _other.Id));
        Assert.False(await _db.Subscriptions.IgnoreQueryFilters().AnyAsync(s => s.TenantId == _other.Id));
    }

    // ── A failed payment ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_payment_is_recorded_with_its_reason_and_gives_nothing()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_1", cycle: 0));
        _notifier.Sent.Clear();

        await _entitlements.ChargeFailedAsync(mandate, Pay("pay_2", status: "failed", error: "Insufficient balance in the account", cycle: 1));

        var failed = (await PaymentsAsync()).Single(p => p.ProviderPaymentId == "pay_2");
        Assert.Equal((PaymentStatus.Failed, "Insufficient balance in the account", "Razorpay", Sub), (failed.Status, failed.FailureReason, failed.Provider, failed.ProviderSubscriptionId));
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(1, await ChargeGrantsAsync());
        // The plan is not touched by a failure: Razorpay retries, and the grace days cover the wait.
        var subscription = await SubscriptionAsync();
        Assert.Equal((SubscriptionStatus.Active, _clock.UtcNow.AddMonths(1)), (subscription.Status, subscription.CurrentPeriodEndUtc!.Value));
        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task The_same_failure_reported_twice_is_one_row()
    {
        var mandate = await MandateAsync();

        await _entitlements.ChargeFailedAsync(mandate, Pay(status: "failed", error: "Declined"));
        await _entitlements.ChargeFailedAsync(mandate, Pay(status: "failed", error: "Declined"));

        Assert.Single(await PaymentsAsync());
    }

    [Fact]
    public async Task A_late_failed_report_never_undoes_a_payment_that_went_through()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        await _entitlements.ChargeFailedAsync(mandate, Pay(status: "failed", error: "Timed out"));

        var payment = Assert.Single(await PaymentsAsync());
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Null(payment.FailureReason);
        Assert.Equal(100m, await BalanceAsync());
    }

    [Fact]
    public async Task A_payment_first_reported_failed_that_then_goes_through_is_one_row_and_is_credited()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeFailedAsync(mandate, Pay(status: "failed", error: "Timed out"));

        await _entitlements.ChargeSucceededAsync(mandate, Pay());

        var payment = Assert.Single(await PaymentsAsync());
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Null(payment.FailureReason);
        Assert.Equal(100m, await BalanceAsync());
    }

    [Fact]
    public async Task A_payment_failed_webhook_is_matched_to_its_mandate_through_razorpays_invoice()
    {
        await MandateAsync();
        _gateway.Invoices["inv_9"] = new RazorpayInvoiceInfo("inv_9", Sub, null, "order_9", "issued", Paise, 0, "INR", null, _t0, _t0, _t0 + 2_592_000, null, null);
        var body = JsonSerializer.Serialize(new
        {
            @event = "payment.failed",
            created_at = _t0 + 30,
            payload = new { payment = new { entity = new { id = "pay_9", amount = Paise, currency = "INR", status = "failed", method = "upi", invoice_id = "inv_9", error_description = "Payment failed" } } }
        });

        var outcome = await _processor.ProcessAsync(Stored(body, "payment.failed"));

        Assert.Equal(RazorpayEventOutcome.Completed, outcome);
        Assert.Equal("inv_9", Assert.Single(_gateway.InvoiceCalls));
        var failed = Assert.Single(await PaymentsAsync());
        Assert.Equal((PaymentStatus.Failed, "pay_9", "inv_9", "Payment failed"), (failed.Status, failed.ProviderPaymentId, failed.ProviderInvoiceId, failed.FailureReason));
        Assert.Equal(_clock.UtcNow.AddDays(30), failed.PeriodEndUtc);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task A_failed_payment_on_an_invoice_that_is_not_a_mandates_is_ignored()
    {
        await MandateAsync();
        _gateway.Invoices["inv_9"] = new RazorpayInvoiceInfo("inv_9", null, null, null, "issued", 5000, 0, "INR", null, null, null, null, null, null);
        var body = "{\"event\":\"payment.failed\",\"payload\":{\"payment\":{\"entity\":{\"id\":\"pay_9\",\"amount\":5000,\"currency\":\"INR\",\"status\":\"failed\",\"invoice_id\":\"inv_9\"}}}}";

        Assert.Equal(RazorpayEventOutcome.Ignored, await _processor.ProcessAsync(Stored(body, "payment.failed")));
        Assert.Empty(await PaymentsAsync());
    }

    [Fact]
    public async Task If_the_invoice_cannot_be_read_the_failed_payment_is_retried_not_dropped()
    {
        await MandateAsync();
        _gateway.InvoiceError = "Could not reach Razorpay";
        var body = "{\"event\":\"payment.failed\",\"payload\":{\"payment\":{\"entity\":{\"id\":\"pay_9\",\"amount\":284026,\"currency\":\"INR\",\"status\":\"failed\",\"invoice_id\":\"inv_9\"}}}}";

        Assert.Equal(RazorpayEventOutcome.Failed, await _processor.ProcessAsync(Stored(body, "payment.failed")));
    }

    [Fact]
    public async Task The_webhook_endpoint_queues_a_failed_payment_that_has_an_invoice()
    {
        var queue = new RecordingWebhookQueue();
        _store.Rows[RazorpayKeyStore.LegacyWebhookSecretKey] = "whsec";
        var receiver = new RazorpayService(_db, _store, _gateway, _clock, NullLogger<RazorpayService>.Instance, queue);
        var body = System.Text.Encoding.UTF8.GetBytes("{\"event\":\"payment.failed\",\"payload\":{\"payment\":{\"entity\":{\"id\":\"pay_9\",\"order_id\":\"order_9\",\"status\":\"failed\",\"invoice_id\":\"inv_9\"}}}}");

        var outcome = await receiver.HandleWebhookAsync(body, RazorpaySignatures.Compute("whsec", body), "evt_F");

        Assert.Equal(RazorpayWebhookOutcome.Queued, outcome);
        Assert.Single(queue.Enqueued);
    }

    // ── The mandate changes ─────────────────────────────────────────────────────────────

    private async Task<RazorpaySubscription> PaidMandateAsync()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay());
        _notifier.Sent.Clear();
        _jobSyncs.Clear();
        return mandate;
    }

    private async Task MoveAsync(RazorpaySubscription mandate, RazorpaySubscriptionStatus to)
    {
        var previous = mandate.Status;
        mandate.Status = to;
        await _db.SaveChangesAsync();
        await _entitlements.MandateChangedAsync(mandate, previous);
    }

    [Fact]
    public async Task While_razorpay_is_retrying_the_plan_keeps_working_and_the_tenant_is_told_once()
    {
        var mandate = await PaidMandateAsync();

        await MoveAsync(mandate, RazorpaySubscriptionStatus.Pending);
        await _entitlements.MandateChangedAsync(mandate, RazorpaySubscriptionStatus.Active);

        Assert.Equal(SubscriptionStatus.Active, (await SubscriptionAsync()).Status);
        Assert.Equal(TenantNotificationKind.AutoPayPaymentFailed, Assert.Single(_notifier.Sent).Kind);
    }

    [Fact]
    public async Task When_every_retry_has_failed_the_plan_is_past_due_and_the_tenant_is_told()
    {
        var mandate = await PaidMandateAsync();

        await MoveAsync(mandate, RazorpaySubscriptionStatus.Halted);

        var subscription = await SubscriptionAsync();
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        // Still on the plan: it ends when what was paid for, and the grace after it, runs out.
        Assert.Equal(_premium.Id, subscription.PlanId);
        Assert.Equal(TenantNotificationKind.AutoPayHalted, Assert.Single(_notifier.Sent).Kind);
        Assert.Equal(100m, await BalanceAsync());
    }

    [Fact]
    public async Task A_mandate_that_starts_paying_again_brings_the_plan_back_from_past_due()
    {
        var mandate = await PaidMandateAsync();
        await MoveAsync(mandate, RazorpaySubscriptionStatus.Halted);

        await MoveAsync(mandate, RazorpaySubscriptionStatus.Active);

        Assert.Equal(SubscriptionStatus.Active, (await SubscriptionAsync()).Status);
    }

    [Fact]
    public async Task A_cancelled_mandate_keeps_the_paid_for_plan_and_only_marks_it_not_renewing()
    {
        var mandate = await PaidMandateAsync();

        await MoveAsync(mandate, RazorpaySubscriptionStatus.Cancelled);

        var subscription = await SubscriptionAsync();
        Assert.Equal((_premium.Id, SubscriptionStatus.Active, true), (subscription.PlanId!.Value, subscription.Status, subscription.CancelAtPeriodEnd));
        Assert.Equal(_clock.UtcNow.AddMonths(1), subscription.CurrentPeriodEndUtc);
        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal(TenantNotificationKind.AutoPayCancelled, notice.Kind);
        Assert.Contains("stays active until", notice.Body);
    }

    [Fact]
    public async Task A_paused_mandate_changes_nothing_but_tells_the_tenant()
    {
        var mandate = await PaidMandateAsync();

        await MoveAsync(mandate, RazorpaySubscriptionStatus.Paused);

        Assert.Equal(SubscriptionStatus.Active, (await SubscriptionAsync()).Status);
        Assert.Equal(TenantNotificationKind.AutoPayPaused, Assert.Single(_notifier.Sent).Kind);
    }

    [Fact]
    public async Task A_mandate_that_never_took_a_payment_says_nothing_about_the_tenants_plan()
    {
        await _credits.AssignTrialPlanAsync(_tenant.Id);
        var mandate = await MandateAsync(status: RazorpaySubscriptionStatus.Created);

        await MoveAsync(mandate, RazorpaySubscriptionStatus.Expired);

        var subscription = await SubscriptionAsync();
        Assert.Equal((_trial.Id, false), (subscription.PlanId!.Value, subscription.CancelAtPeriodEnd));
        Assert.Empty(_notifier.Sent);
    }

    // ── The paid plan runs out ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_halted_mandate_ends_the_plan_only_after_the_paid_period_and_the_grace_days()
    {
        var mandate = await PaidMandateAsync();
        await MoveAsync(mandate, RazorpaySubscriptionStatus.Halted);
        _notifier.Sent.Clear();

        // The day the period ends, and through the grace days: still Premium.
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(AutoPayOptions.GraceDays).AddMinutes(-1);
        Assert.Equal(0, await _maintenance.EndLapsedPlansAsync());
        Assert.Equal(_premium.Id, (await SubscriptionAsync()).PlanId);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
        Assert.Equal(1, await _maintenance.EndLapsedPlansAsync());

        var subscription = await SubscriptionAsync();
        Assert.Equal((_trial.Id, SubscriptionStatus.Active), (subscription.PlanId!.Value, subscription.Status));
        Assert.Null(subscription.RazorpaySubscriptionRowId);
        Assert.False(subscription.CancelAtPeriodEnd);
        Assert.Null(subscription.CurrentPeriodEndUtc);
        Assert.Equal(TenantNotificationKind.PlanRenewalFailed, Assert.Single(_notifier.Sent).Kind);
        Assert.Equal(_tenant.Id, Assert.Single(_jobSyncs));

        // Once, not every hour after.
        Assert.Equal(0, await _maintenance.EndLapsedPlansAsync());
    }

    [Fact]
    public async Task Leaving_the_paid_plan_never_takes_credits_back()
    {
        var mandate = await PaidMandateAsync();
        await MoveAsync(mandate, RazorpaySubscriptionStatus.Cancelled);
        _clock.UtcNow = _clock.UtcNow.AddMonths(2);

        await _maintenance.EndLapsedPlansAsync();

        // The 100 paid for, plus the Trial plan's own starter credits the first time the tenant is on it.
        Assert.Equal(150m, await BalanceAsync());
    }

    [Fact]
    public async Task A_plan_whose_mandate_is_still_running_is_never_ended_however_late_its_payment_looks()
    {
        await PaidMandateAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(3);

        Assert.Equal(0, await _maintenance.EndLapsedPlansAsync());
        Assert.Equal(_premium.Id, (await SubscriptionAsync()).PlanId);
    }

    [Fact]
    public async Task A_payment_that_lands_before_the_pass_runs_keeps_the_plan()
    {
        var mandate = await PaidMandateAsync();
        await MoveAsync(mandate, RazorpaySubscriptionStatus.Halted);
        _clock.UtcNow = _clock.UtcNow.AddMonths(1).AddDays(10);
        // The tenant fixed their account and Razorpay took the money: the period moves on.
        await _entitlements.ChargeSucceededAsync(mandate, Pay("pay_2", cycle: 1));

        Assert.Equal(0, await _maintenance.EndLapsedPlansAsync());
        var subscription = await SubscriptionAsync();
        Assert.Equal((_premium.Id, SubscriptionStatus.Active), (subscription.PlanId!.Value, subscription.Status));
    }

    [Fact]
    public async Task With_no_trial_plan_to_fall_back_to_the_paid_plan_simply_ends()
    {
        var mandate = await PaidMandateAsync();
        await MoveAsync(mandate, RazorpaySubscriptionStatus.Cancelled);
        (await _db.Plans.SingleAsync(p => p.Id == _trial.Id)).Code = "not-the-trial";
        await _db.SaveChangesAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(2);

        Assert.Equal(1, await _maintenance.EndLapsedPlansAsync());

        var subscription = await SubscriptionAsync();
        Assert.Null(subscription.PlanId);
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.Null(subscription.RazorpaySubscriptionRowId);
    }

    [Fact]
    public async Task After_lapsing_a_new_mandates_first_payment_puts_the_tenant_back_on_the_plan()
    {
        var old = await PaidMandateAsync();
        await MoveAsync(old, RazorpaySubscriptionStatus.Cancelled);
        _clock.UtcNow = _clock.UtcNow.AddMonths(2);
        await _maintenance.EndLapsedPlansAsync();

        var fresh = await MandateAsync("sub_T000002");
        await _entitlements.ChargeSucceededAsync(fresh, Pay("pay_7"));

        var subscription = await SubscriptionAsync();
        Assert.Equal((_premium.Id, fresh.Id), (subscription.PlanId!.Value, subscription.RazorpaySubscriptionRowId!.Value));
        Assert.Equal(_clock.UtcNow.AddMonths(1), subscription.CurrentPeriodEndUtc);
    }

    // ── Reconciliation recovers a payment whose webhook never came ──────────────────────

    private void Invoice(string id, string paymentId, int cycle, string status = "paid") =>
        _gateway.Invoices[id] = new RazorpayInvoiceInfo(
            id, Sub, status == "paid" ? paymentId : null, "order_" + id[4..], status, Paise, status == "paid" ? Paise : 0, "INR",
            status == "paid" ? Unix(_clock.UtcNow.AddMonths(cycle).AddMinutes(5)) : null, Unix(_clock.UtcNow.AddMonths(cycle)),
            Unix(_clock.UtcNow.AddMonths(cycle)), Unix(_clock.UtcNow.AddMonths(cycle + 1)), "https://rzp.io/i/" + id, null);

    [Fact]
    public async Task Reconciliation_records_a_payment_razorpay_took_that_never_reached_us_with_its_credits()
    {
        var mandate = await PaidMandateAsync();
        // Razorpay has taken a second payment; its webhook never arrived.
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { PaidCount = 2 };
        Invoice("inv_1", "pay_1", 0);
        Invoice("inv_2", "pay_2", 1);
        Invoice("inv_3", "pay_3", 2, status: "issued");

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal((1, 0), (result.PaymentsRecovered, result.PaymentsMissing));
        var payments = await PaymentsAsync();
        Assert.Equal(new[] { "pay_1", "pay_2" }, payments.Select(p => p.ProviderPaymentId).ToArray());
        var recovered = payments[1];
        Assert.Equal(("inv_2", PaymentStatus.Succeeded, 2840.26m), (recovered.ProviderInvoiceId, recovered.Status, recovered.TotalLocal));
        Assert.Equal((_clock.UtcNow.AddMonths(1), _clock.UtcNow.AddMonths(2)), (recovered.PeriodStartUtc!.Value, recovered.PeriodEndUtc!.Value));
        Assert.Equal(200m, await BalanceAsync());
        Assert.Equal(_clock.UtcNow.AddMonths(2), (await SubscriptionAsync()).CurrentPeriodEndUtc);
        Assert.Equal(mandate.Id, (await SubscriptionAsync()).RazorpaySubscriptionRowId);
    }

    [Fact]
    public async Task A_recovered_payment_whose_webhook_then_turns_up_is_not_counted_twice()
    {
        await MandateAsync(status: RazorpaySubscriptionStatus.Active);
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { PaidCount = 1 };
        Invoice("inv_1", "pay_1", 0);
        Assert.Equal(1, (await _maintenance.ReconcileAsync()).PaymentsRecovered);

        // The late webhook, and another reconciliation for good measure.
        await _processor.ProcessAsync(Stored(ChargedBody("pay_1", 20)));
        var again = await _maintenance.ReconcileAsync();

        Assert.Equal(0, again.PaymentsRecovered);
        Assert.Single(await PaymentsAsync());
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(1, await ChargeGrantsAsync());
    }

    [Fact]
    public async Task When_both_sides_agree_reconciliation_does_not_even_ask_for_invoices()
    {
        await PaidMandateAsync();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { PaidCount = 1 };

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal((0, 0), (result.PaymentsRecovered, result.PaymentsMissing));
        Assert.Empty(_gateway.InvoiceListCalls);
    }

    // ── The simulated paths and refunds ─────────────────────────────────────────────────

    [Fact]
    public async Task A_plan_cannot_be_switched_directly_while_its_mandate_is_still_charging()
    {
        await PaidMandateAsync();
        var ledger = new QuotaLedgerService(_db, _clock);
        var billing = new BillingService(_db, new PlatformContext(), _clock, _jobs, ledger, TestPricing.Default(), _credits);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => billing.ChoosePlanAsync(_tenant.Id, _trial.Id));

        Assert.Contains("Cancel the automatic payment", ex.Message);
        _db.ChangeTracker.Clear();
        Assert.Equal(_premium.Id, (await SubscriptionAsync()).PlanId);
    }

    [Fact]
    public async Task Once_the_mandate_has_ended_the_plan_can_be_switched_and_the_link_is_dropped()
    {
        var mandate = await PaidMandateAsync();
        await MoveAsync(mandate, RazorpaySubscriptionStatus.Cancelled);
        var billing = new BillingService(_db, new PlatformContext(), _clock, _jobs, new QuotaLedgerService(_db, _clock), TestPricing.Default(), _credits);

        await billing.ChoosePlanAsync(_tenant.Id, _trial.Id);

        var subscription = await SubscriptionAsync();
        Assert.Equal(_trial.Id, subscription.PlanId);
        Assert.Null(subscription.RazorpaySubscriptionRowId);
        Assert.False(subscription.CancelAtPeriodEnd);
    }

    [Fact]
    public async Task A_razorpay_payment_cannot_be_refunded_through_the_simulated_refund_gateway()
    {
        _tenant.RefundRequestsEnabled = true;
        await _db.SaveChangesAsync();
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay());
        await _entitlements.ChargeFailedAsync(mandate, Pay("pay_2", status: "failed", error: "Declined"));
        var gateway = new FakeRefundGateway();
        var refunds = new RefundService(_db, _ledger, gateway, _notifier, _clock, new FixedOptions<RefundPolicyOptions>(new()), new RecordingPlatformNotifier());
        var payments = await PaymentsAsync();

        var paid = await refunds.GetEligibilityAsync(_tenant.Id, payments[0].Id);
        var failed = await refunds.GetEligibilityAsync(_tenant.Id, payments[1].Id);

        Assert.False(paid.Eligible);
        Assert.Contains("contact support", paid.Reason);
        Assert.False(failed.Eligible);
        await Assert.ThrowsAsync<ConflictException>(() => refunds.RefundDirectAsync(_tenant.Id, payments[0].Id, "operator", Guid.NewGuid()));
        Assert.Equal(0, gateway.Calls);
    }

    // ── What the tenant sees ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Payment_history_shows_the_status_method_reason_and_references()
    {
        var mandate = await MandateAsync();
        await _entitlements.ChargeSucceededAsync(mandate, Pay());
        await _entitlements.ChargeFailedAsync(mandate, Pay("pay_2", status: "failed", error: "Declined by bank"));
        var billing = new BillingService(_db, new PlatformContext(), _clock, _jobs, _ledger, TestPricing.Default(), _credits);

        var history = (await billing.GetPaymentHistoryForTenantAsync(_tenant.Id)).OrderBy(p => p.TransactionId).ToList();

        Assert.Equal(("Succeeded", "upi", null, "pay_1", "inv_1", "Razorpay"), (history[0].Status, history[0].PaymentMethod, history[0].FailureReason, history[0].TransactionId, history[0].InvoiceId, history[0].Provider));
        Assert.Equal(("Failed", "Declined by bank", "pay_2"), (history[1].Status, history[1].FailureReason, history[1].TransactionId));
    }

    [Fact]
    public async Task Invoices_come_from_razorpay_newest_first_and_only_for_the_tenants_own_mandate()
    {
        var mandate = await MandateAsync();
        Invoice("inv_1", "pay_1", 0);
        Invoice("inv_2", "pay_2", 1);
        var service = new AutoPayService(_db, _store, _gateway, TestPricing.Default(), _clock, new FixedOptions<AutoPayOptions>(new()), NullLogger<AutoPayService>.Instance);

        var invoices = await service.GetInvoicesAsync(_tenant.Id, mandate.Id);

        Assert.Equal(new[] { "inv_2", "inv_1" }, invoices.Select(i => i.InvoiceId).ToArray());
        Assert.Equal(("paid", 2840.26m, 2840.26m, "INR", "pay_2", "https://rzp.io/i/inv_2"),
            (invoices[0].Status, invoices[0].Amount, invoices[0].AmountPaid, invoices[0].CurrencyCode, invoices[0].TransactionId, invoices[0].Url));
        Assert.Equal(_clock.UtcNow.AddMonths(2), invoices[0].PeriodEndUtc);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetInvoicesAsync(_other.Id, mandate.Id));
    }

    [Fact]
    public async Task If_razorpay_cannot_be_reached_the_invoice_list_says_so_rather_than_showing_none()
    {
        var mandate = await MandateAsync();
        _gateway.InvoiceError = "Could not reach Razorpay";
        var service = new AutoPayService(_db, _store, _gateway, TestPricing.Default(), _clock, new FixedOptions<AutoPayOptions>(new()), NullLogger<AutoPayService>.Instance);

        await Assert.ThrowsAsync<ConflictException>(() => service.GetInvoicesAsync(_tenant.Id, mandate.Id));
    }
}
