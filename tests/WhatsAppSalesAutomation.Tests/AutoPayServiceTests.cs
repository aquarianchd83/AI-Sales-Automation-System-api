using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Billing.AutoPay;
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
/// Starting, reading and cancelling a UPI AutoPay mandate, against the real model and a Razorpay that lives in memory. Premium is priced at
/// Rs 2,407 for India; with GST at 18% the tenant is debited Rs 2,840.26 - 284026 paise - each month.
/// </summary>
public sealed class AutoPayServiceTests : IDisposable
{
    private const string TestKeyId = "rzp_test_1DP5mmOlF5G5ag";
    private const string LiveKeyId = "rzp_live_1DP5mmOlF5G5ag";
    private const long PremiumPaise = 284026;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly MemorySettingsStore _store = new();
    private readonly FakeSubscriptionGateway _gateway = new();
    private readonly AutoPayOptions _options = new();
    private readonly AutoPayService _service;
    private readonly BillingService _billing;

    private readonly Tenant _tenant = new() { Name = "Mumbai Co", Slug = "mumbai", CountryCode = "IN", StateCode = "MH", Status = TenantStatus.Trial };
    private readonly Tenant _other = new() { Name = "Delhi Co", Slug = "delhi", CountryCode = "IN", StateCode = "DL", Status = TenantStatus.Trial };
    private readonly Tenant _british = new() { Name = "British Co", Slug = "british", CountryCode = "GB", Status = TenantStatus.Trial };
    private readonly Plan _trial = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Trial", StarterCredits = 50m, IsActive = true };
    private readonly Plan _premium = new() { Code = PlatformFeatureCatalog.PremiumPlanCode, Name = "Premium", PriceMonthlyCents = 2900, MonthlyCredits = 100m, IsActive = true };
    private readonly Plan _enterprise = new() { Code = "enterprise", Name = "Enterprise", PriceMonthlyCents = 30000, IsActive = true };
    private readonly Plan _retired = new() { Code = "legacy", Name = "Legacy", IsActive = false };
    private readonly Guid _userId = Guid.NewGuid();

    public AutoPayServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _service = new AutoPayService(_db, _store, _gateway, TestPricing.Default(), _clock, new FixedOptions<AutoPayOptions>(_options), NullLogger<AutoPayService>.Instance);

        var ledger = new QuotaLedgerService(_db, _clock);
        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        _billing = new BillingService(_db, new PlatformContext(), _clock, jobs, ledger, TestPricing.Default(), new CreditAllocationService(_db, ledger, _clock), null, _service);

        _db.Tenants.AddRange(_tenant, _other, _british);
        _db.Plans.AddRange(_trial, _premium, _enterprise, _retired);
        _db.PlanPrices.Add(new PlanPrice { PlanId = _trial.Id, CountryCode = "IN", Amount = 0m });
        _db.PlanPrices.Add(new PlanPrice { PlanId = _premium.Id, CountryCode = "IN", Amount = 2407m });
        _db.PlanPrices.Add(new PlanPrice { PlanId = _premium.Id, CountryCode = "GB", Amount = 23m });
        _db.PlanPrices.Add(new PlanPrice { PlanId = _enterprise.Id, CountryCode = "IN", Amount = 20000m });
        _db.SaveChanges();

        SwitchOn(TestKeyId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void SwitchOn(string keyId)
    {
        _store.Rows[RazorpayService.KeyIdKey] = keyId;
        _store.Rows[RazorpayService.KeySecretKey] = "key-secret-123";
        _store.Rows[AutoPayService.EnabledKey] = "true";
    }

    private Task<AutoPayCheckoutDto> CheckoutAsync(Tenant? tenant = null, Plan? plan = null) =>
        _service.CheckoutAsync((tenant ?? _tenant).Id, (plan ?? _premium).Id, _userId);

    private Task<List<RazorpaySubscription>> RowsAsync(Tenant? tenant = null) =>
        _db.RazorpaySubscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == (tenant ?? _tenant).Id).ToListAsync();

    /// <summary>Razorpay's side of a mandate changing: the customer authorised it, a cycle started, and so on.</summary>
    private void AtRazorpay(string subscriptionId, Func<RazorpaySubscriptionInfo, RazorpaySubscriptionInfo> change) =>
        _gateway.Subscriptions[subscriptionId] = change(_gateway.Subscriptions[subscriptionId]);

    private static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    // ── Checkout: what is created ───────────────────────────────────────────────────────

    [Fact]
    public async Task Checkout_creates_the_plan_at_the_database_price_plus_tax_in_paise()
    {
        var checkout = await CheckoutAsync();

        var plan = Assert.Single(_gateway.PlanCalls);
        Assert.Equal(("monthly", 1, PremiumPaise, "INR"), (plan.Period, plan.Interval, plan.Amount, plan.Currency));
        Assert.Equal(PremiumPaise, checkout.AmountMinor);
        Assert.Equal("INR", checkout.Currency);

        var mapped = await _db.RazorpayPlans.AsNoTracking().SingleAsync();
        Assert.Equal((_premium.Id, "test", PremiumPaise, "plan_T000001"), (mapped.PlanId, mapped.Mode, mapped.AmountMinor, mapped.RazorpayPlanId));
    }

    [Fact]
    public async Task Checkout_creates_the_subscription_and_returns_where_to_authorise_it()
    {
        var checkout = await CheckoutAsync();

        var call = Assert.Single(_gateway.SubscriptionCalls);
        Assert.Equal("plan_T000001", call.PlanId);
        Assert.Equal(120, call.TotalCount);
        Assert.False(call.CustomerNotify);
        Assert.Equal(Unix(_clock.UtcNow.AddHours(48)), call.ExpireBy);
        Assert.Equal(_tenant.Id.ToString("N"), call.Notes["tenant_id"]);
        Assert.Equal("premium", call.Notes["plan_code"]);

        Assert.Equal("sub_T000001", checkout.SubscriptionId);
        Assert.Equal(AutoPayDisplayStatus.PendingAuthorization, checkout.Status);
        Assert.Equal("https://rzp.io/i/sub_T000001", checkout.AuthorizationUrl);
        Assert.Equal(TestKeyId, checkout.KeyId);
        Assert.Equal("test", checkout.Mode);
        Assert.Equal("Premium", checkout.PlanName);
    }

    [Fact]
    public async Task Checkout_saves_the_mandate_as_waiting_and_gives_the_tenant_nothing_yet()
    {
        var checkout = await CheckoutAsync();

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(checkout.Id, row.Id);
        Assert.Equal(RazorpaySubscriptionStatus.Created, row.Status);
        Assert.Equal((_premium.Id, PremiumPaise, "INR", "test", 120, _userId), (row.PlanId, row.AmountMinor, row.Currency, row.Mode, row.TotalCount, row.CreatedByUserId!.Value));
        Assert.Null(row.AuthenticatedAtUtc);

        // Starting a mandate is not paying: no plan, no payment, no credits.
        Assert.False(await _db.Subscriptions.IgnoreQueryFilters().AnyAsync());
        Assert.False(await _db.Payments.IgnoreQueryFilters().AnyAsync());
        Assert.False(await _db.QuotaLedgerEntries.IgnoreQueryFilters().AnyAsync());
        Assert.Equal(TenantStatus.Trial, (await _db.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenant.Id)).Status);
    }

    [Fact]
    public async Task A_second_tenant_paying_the_same_amount_reuses_the_razorpay_plan()
    {
        await CheckoutAsync(_tenant);
        // Delhi pays IGST where Mumbai pays CGST + SGST: the same total, so the same plan.
        await CheckoutAsync(_other);

        Assert.Single(_gateway.PlanCalls);
        Assert.Equal(1, await _db.RazorpayPlans.CountAsync());
        Assert.Equal(2, _gateway.SubscriptionCalls.Count);
        Assert.All(_gateway.SubscriptionCalls, c => Assert.Equal("plan_T000001", c.PlanId));
    }

    [Fact]
    public async Task A_new_price_gets_a_new_razorpay_plan_and_leaves_the_old_one_alone()
    {
        await CheckoutAsync(_tenant);
        (await _db.PlanPrices.SingleAsync(p => p.PlanId == _premium.Id && p.CountryCode == "IN")).Amount = 3000m;
        await _db.SaveChangesAsync();

        await CheckoutAsync(_other);

        Assert.Equal(new[] { PremiumPaise, 354000L }, _gateway.PlanCalls.Select(p => p.Amount).ToArray());
        Assert.Equal(2, await _db.RazorpayPlans.CountAsync());
    }

    [Fact]
    public async Task With_no_expiry_window_razorpay_is_left_to_decide()
    {
        _options.AuthorizationExpiryHours = 0;

        await CheckoutAsync();

        Assert.Null(Assert.Single(_gateway.SubscriptionCalls).ExpireBy);
    }

    // ── Checkout: who may not ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Checkout_is_refused_while_autopay_is_switched_off()
    {
        _store.Rows[AutoPayService.EnabledKey] = "false";

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Empty(_gateway.PlanCalls);
        Assert.Empty(_gateway.SubscriptionCalls);
    }

    [Fact]
    public async Task Autopay_is_off_until_someone_switches_it_on()
    {
        _store.Rows.Remove(AutoPayService.EnabledKey);

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());
        Assert.False(await _service.IsRequiredAsync("INR"));
    }

    [Fact]
    public async Task Checkout_is_refused_without_razorpay_keys()
    {
        _store.Rows.Remove(RazorpayService.KeySecretKey);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Contains("not set up", ex.Message);
        Assert.Empty(_gateway.SubscriptionCalls);
    }

    [Fact]
    public async Task A_plan_over_the_upi_limit_is_refused_before_anything_is_created()
    {
        // 20,000 + 18% = 23,600 a month; UPI AutoPay carries 15,000.
        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync(plan: _enterprise));

        Assert.Contains("15,000", ex.Message);
        Assert.Empty(_gateway.PlanCalls);
        Assert.Empty(_gateway.SubscriptionCalls);
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task The_upi_limit_is_configurable()
    {
        _options.MaxRecurringAmountInr = 100_000m;

        var checkout = await CheckoutAsync(plan: _enterprise);

        Assert.Equal(2_360_000, checkout.AmountMinor);
    }

    [Fact]
    public async Task A_tenant_not_billed_in_rupees_cannot_use_upi_autopay()
    {
        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync(_british));

        Assert.Contains("rupees", ex.Message);
        Assert.Empty(_gateway.PlanCalls);
    }

    [Fact]
    public async Task A_free_plan_has_nothing_to_pay_automatically()
    {
        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync(plan: _trial));

        Assert.Empty(_gateway.PlanCalls);
    }

    [Fact]
    public async Task A_plan_with_no_price_for_the_country_is_not_sold()
    {
        _db.PlanPrices.Remove(await _db.PlanPrices.SingleAsync(p => p.PlanId == _premium.Id && p.CountryCode == "IN"));
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());
    }

    [Fact]
    public async Task A_retired_or_unknown_plan_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => CheckoutAsync(plan: _retired));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.CheckoutAsync(_tenant.Id, Guid.NewGuid(), _userId));
    }

    // ── Checkout: an existing mandate ───────────────────────────────────────────────────

    [Fact]
    public async Task Asking_again_while_one_waits_returns_the_same_mandate()
    {
        var first = await CheckoutAsync();
        var second = await CheckoutAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.AuthorizationUrl, second.AuthorizationUrl);
        Assert.Single(_gateway.SubscriptionCalls);
        Assert.Single(await RowsAsync());
    }

    [Fact]
    public async Task A_running_mandate_blocks_a_second_one()
    {
        var first = await CheckoutAsync();
        AtRazorpay(first.SubscriptionId, s => s with { Status = "active", PaidCount = 1 });

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Contains("already", ex.Message);
        Assert.Single(_gateway.SubscriptionCalls);
        // And we learnt what Razorpay knew: the mandate it authorised is no longer shown as waiting.
        Assert.Equal(RazorpaySubscriptionStatus.Active, Assert.Single(await RowsAsync()).Status);
    }

    [Fact]
    public async Task A_mandate_razorpay_expired_no_longer_blocks_a_new_one()
    {
        var first = await CheckoutAsync();
        AtRazorpay(first.SubscriptionId, s => s with { Status = "expired" });

        var second = await CheckoutAsync();

        Assert.NotEqual(first.Id, second.Id);
        var rows = await RowsAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Expired, rows.Single(r => r.Id == first.Id).Status);
        Assert.Equal(RazorpaySubscriptionStatus.Created, rows.Single(r => r.Id == second.Id).Status);
        Assert.Empty(_gateway.CancelCalls);
    }

    [Fact]
    public async Task A_waiting_mandate_at_an_old_price_is_cancelled_at_razorpay_before_a_new_one_is_offered()
    {
        var first = await CheckoutAsync();
        (await _db.PlanPrices.SingleAsync(p => p.PlanId == _premium.Id && p.CountryCode == "IN")).Amount = 3000m;
        await _db.SaveChangesAsync();

        var second = await CheckoutAsync();

        Assert.Equal((first.SubscriptionId, false), Assert.Single(_gateway.CancelCalls));
        Assert.Equal(354000, second.AmountMinor);
        var rows = await RowsAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Cancelled, rows.Single(r => r.Id == first.Id).Status);
        Assert.Equal(RazorpaySubscriptionStatus.Created, rows.Single(r => r.Id == second.Id).Status);
    }

    [Fact]
    public async Task If_the_old_waiting_mandate_cannot_be_cancelled_no_second_one_is_offered()
    {
        await CheckoutAsync();
        (await _db.PlanPrices.SingleAsync(p => p.PlanId == _premium.Id && p.CountryCode == "IN")).Amount = 3000m;
        await _db.SaveChangesAsync();
        _gateway.CancelError = "Gateway timeout";

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Single(_gateway.SubscriptionCalls);
        Assert.Equal(RazorpaySubscriptionStatus.Created, Assert.Single(await RowsAsync()).Status);
    }

    [Fact]
    public async Task If_razorpay_cannot_be_asked_about_an_existing_mandate_nothing_new_is_created()
    {
        await CheckoutAsync();
        _gateway.FetchError = "Could not reach Razorpay";

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Single(_gateway.SubscriptionCalls);
    }

    [Fact]
    public async Task A_test_mandate_does_not_stand_in_the_way_once_the_platform_goes_live()
    {
        var test = await CheckoutAsync();
        SwitchOn(LiveKeyId);

        var live = await CheckoutAsync();

        Assert.Equal("live", live.Mode);
        // The test one cannot be reached with live keys, so it is neither fetched nor cancelled - just closed here.
        Assert.Empty(_gateway.FetchCalls);
        Assert.Empty(_gateway.CancelCalls);
        var rows = await RowsAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Expired, rows.Single(r => r.Id == test.Id).Status);
        Assert.Equal(RazorpaySubscriptionStatus.Created, rows.Single(r => r.Id == live.Id).Status);
        // And the live keys get a plan of their own: a test plan id means nothing to the live account.
        Assert.Equal(2, _gateway.PlanCalls.Count);
    }

    // ── Checkout: Razorpay says no ──────────────────────────────────────────────────────

    [Fact]
    public async Task When_razorpay_refuses_the_plan_its_reason_comes_back_and_nothing_is_saved()
    {
        _gateway.CreatePlanError = "Authentication failed";

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Contains("Authentication failed", ex.Message);
        Assert.False(await _db.RazorpayPlans.AnyAsync());
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task When_razorpay_refuses_the_subscription_no_mandate_is_saved()
    {
        _gateway.CreateSubscriptionError = "The requested URL was not found on the server.";

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Contains("not found on the server", ex.Message);
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task A_plan_razorpay_created_at_the_wrong_amount_is_not_used()
    {
        _gateway.PlanAmountOverride = 1;

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.False(await _db.RazorpayPlans.AnyAsync());
        Assert.Empty(_gateway.SubscriptionCalls);
    }

    [Fact]
    public async Task Losing_a_race_to_another_checkout_cancels_ours_at_razorpay()
    {
        // Another request for the same tenant saves its waiting mandate after our checks and before our save.
        _gateway.BeforeCreateSubscription = () =>
        {
            _gateway.BeforeCreateSubscription = null;
            var planRow = _db.RazorpayPlans.AsNoTracking().Single();
            _db.RazorpaySubscriptions.Add(new RazorpaySubscription
            {
                TenantId = _tenant.Id, PlanId = _premium.Id, RazorpayPlanRowId = planRow.Id, RazorpaySubscriptionId = "sub_RIVAL",
                AmountMinor = PremiumPaise, TotalCount = 120, ShortUrl = "https://rzp.io/i/sub_RIVAL"
            });
            _db.SaveChanges();
        };

        await Assert.ThrowsAsync<ConflictException>(() => CheckoutAsync());

        Assert.Equal(("sub_T000001", false), Assert.Single(_gateway.CancelCalls));
        Assert.Equal("sub_RIVAL", Assert.Single(await RowsAsync()).RazorpaySubscriptionId);
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_that_never_started_a_mandate_has_none()
    {
        Assert.Null(await _service.GetCurrentAsync(_tenant.Id));
    }

    [Fact]
    public async Task The_current_mandate_shows_as_waiting_with_its_authorisation_link()
    {
        var checkout = await CheckoutAsync();

        var current = await _service.GetCurrentAsync(_tenant.Id);

        Assert.NotNull(current);
        Assert.Equal(checkout.Id, current!.Id);
        Assert.Equal(AutoPayDisplayStatus.PendingAuthorization, current.Status);
        Assert.Equal("Premium", current.PlanName);
        Assert.Equal(2840.26m, current.Amount);
        Assert.Equal("Monthly", current.BillingFrequency);
        Assert.Equal(checkout.AuthorizationUrl, current.AuthorizationUrl);
        Assert.False(current.AutoRenew);
        Assert.Null(current.NextRenewalUtc);
    }

    [Fact]
    public async Task Refresh_records_what_razorpay_says_not_what_the_browser_might_claim()
    {
        var checkout = await CheckoutAsync();
        var start = new DateTime(2026, 9, 10, 12, 5, 0, DateTimeKind.Utc);
        AtRazorpay(checkout.SubscriptionId, s => s with
        {
            Status = "active", PaidCount = 1, RemainingCount = 119, CurrentStart = Unix(start), CurrentEnd = Unix(start.AddMonths(1)),
            ChargeAt = Unix(start.AddMonths(1)), CustomerId = "cust_1", PaymentMethod = "upi"
        });

        var refreshed = await _service.RefreshAsync(_tenant.Id, checkout.Id);

        Assert.Equal(AutoPayDisplayStatus.Active, refreshed.Status);
        Assert.Equal("Active", refreshed.GatewayStatus);
        Assert.Equal(start, refreshed.CurrentPeriodStartUtc);
        Assert.Equal(start.AddMonths(1), refreshed.CurrentPeriodEndUtc);
        Assert.Equal(start.AddMonths(1), refreshed.NextRenewalUtc);
        Assert.True(refreshed.AutoRenew);
        Assert.Equal("upi", refreshed.PaymentMethod);
        Assert.Null(refreshed.AuthorizationUrl);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((1, "cust_1"), (row.PaidCount, row.CustomerId));
        Assert.Equal(_clock.UtcNow, row.AuthenticatedAtUtc);
        Assert.Equal(_clock.UtcNow, row.ActivatedAtUtc);
    }

    [Fact]
    public async Task Refresh_leaves_a_mandate_waiting_while_razorpay_still_says_created()
    {
        var checkout = await CheckoutAsync();

        var refreshed = await _service.RefreshAsync(_tenant.Id, checkout.Id);

        Assert.Equal(AutoPayDisplayStatus.PendingAuthorization, refreshed.Status);
    }

    [Fact]
    public async Task Refresh_moves_the_mandate_only_never_the_plan_or_the_wallet()
    {
        var checkout = await CheckoutAsync();
        AtRazorpay(checkout.SubscriptionId, s => s with { Status = "active", PaidCount = 1 });

        await _service.RefreshAsync(_tenant.Id, checkout.Id);

        Assert.False(await _db.Subscriptions.IgnoreQueryFilters().AnyAsync());
        Assert.False(await _db.Payments.IgnoreQueryFilters().AnyAsync());
        Assert.False(await _db.QuotaLedgerEntries.IgnoreQueryFilters().AnyAsync());
    }

    [Fact]
    public async Task Refresh_fails_loudly_when_razorpay_cannot_be_reached()
    {
        var checkout = await CheckoutAsync();
        _gateway.FetchError = "Could not reach Razorpay";

        await Assert.ThrowsAsync<ConflictException>(() => _service.RefreshAsync(_tenant.Id, checkout.Id));

        Assert.Equal(RazorpaySubscriptionStatus.Created, Assert.Single(await RowsAsync()).Status);
    }

    [Fact]
    public async Task An_unknown_status_from_razorpay_changes_nothing()
    {
        var checkout = await CheckoutAsync();
        AtRazorpay(checkout.SubscriptionId, s => s with { Status = "something_new" });

        var refreshed = await _service.RefreshAsync(_tenant.Id, checkout.Id);

        Assert.Equal("Created", refreshed.GatewayStatus);
    }

    // ── Tenant isolation ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_tenant_cannot_read_refresh_or_cancel_anothers_mandate()
    {
        var checkout = await CheckoutAsync(_tenant);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetAsync(_other.Id, checkout.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.RefreshAsync(_other.Id, checkout.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.CancelAsync(_other.Id, checkout.Id, false));

        Assert.Empty(_gateway.FetchCalls);
        Assert.Empty(_gateway.CancelCalls);
        Assert.Null(await _service.GetCurrentAsync(_other.Id));
        Assert.Equal(RazorpaySubscriptionStatus.Created, Assert.Single(await RowsAsync()).Status);
    }

    // ── Cancelling ──────────────────────────────────────────────────────────────────────

    private async Task<AutoPayCheckoutDto> ActiveMandateAsync(bool linkToPlan = false)
    {
        var checkout = await CheckoutAsync();
        var start = _clock.UtcNow;
        AtRazorpay(checkout.SubscriptionId, s => s with
        {
            Status = "active", PaidCount = 1, CurrentStart = Unix(start), CurrentEnd = Unix(start.AddMonths(1)), ChargeAt = Unix(start.AddMonths(1))
        });
        await _service.RefreshAsync(_tenant.Id, checkout.Id);

        if (linkToPlan)
        {
            // What a confirmed first payment will have done: the tenant is on Premium, paid through to the end of the cycle.
            _db.Subscriptions.Add(new Subscription
            {
                TenantId = _tenant.Id, PlanId = _premium.Id, Status = SubscriptionStatus.Active, RazorpaySubscriptionRowId = checkout.Id,
                CurrentPeriodStartUtc = start, CurrentPeriodEndUtc = start.AddMonths(1)
            });
            await _db.SaveChangesAsync();
        }

        return checkout;
    }

    [Fact]
    public async Task Cancelling_at_cycle_end_stops_renewal_and_keeps_the_mandate_running()
    {
        var checkout = await ActiveMandateAsync();

        var cancelled = await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: true);

        Assert.Equal((checkout.SubscriptionId, true), Assert.Single(_gateway.CancelCalls));
        Assert.Equal(AutoPayDisplayStatus.CancellationScheduled, cancelled.Status);
        Assert.Equal("Active", cancelled.GatewayStatus);
        Assert.True(cancelled.CancelAtCycleEnd);
        Assert.False(cancelled.AutoRenew);
        Assert.Null(cancelled.NextRenewalUtc);
        Assert.Equal(_clock.UtcNow.AddMonths(1), cancelled.CurrentPeriodEndUtc);
        Assert.Null(cancelled.CancelledAtUtc);
    }

    [Fact]
    public async Task Cancelling_never_takes_away_what_has_been_paid_for()
    {
        var checkout = await ActiveMandateAsync(linkToPlan: true);

        await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: false);

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(_premium.Id, subscription.PlanId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(_clock.UtcNow.AddMonths(1), subscription.CurrentPeriodEndUtc);
        Assert.True(subscription.CancelAtPeriodEnd);
    }

    [Fact]
    public async Task Cancelling_now_ends_the_mandate()
    {
        var checkout = await ActiveMandateAsync();

        var cancelled = await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: false);

        Assert.Equal((checkout.SubscriptionId, false), Assert.Single(_gateway.CancelCalls));
        Assert.Equal(AutoPayDisplayStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.AutoRenew);
        Assert.NotNull(cancelled.CancelledAtUtc);

        var row = Assert.Single(await RowsAsync());
        Assert.NotNull(row.EndedAtUtc);
        Assert.Null(row.ChargeAtUtc);
    }

    [Fact]
    public async Task A_mandate_never_authorised_is_cancelled_outright_whatever_was_asked()
    {
        var checkout = await CheckoutAsync();

        var cancelled = await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: true);

        // There is no paid cycle to wait for the end of.
        Assert.Equal((checkout.SubscriptionId, false), Assert.Single(_gateway.CancelCalls));
        Assert.Equal(AutoPayDisplayStatus.Cancelled, cancelled.Status);
        // And the tenant can start again.
        Assert.NotEqual(checkout.Id, (await CheckoutAsync()).Id);
    }

    [Fact]
    public async Task If_razorpay_refuses_to_cancel_nothing_changes_here()
    {
        var checkout = await ActiveMandateAsync(linkToPlan: true);
        _gateway.CancelError = "Subscription is not cancellable in this state";

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: true));

        Assert.Contains("not cancellable", ex.Message);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(RazorpaySubscriptionStatus.Active, row.Status);
        Assert.False(row.CancelAtCycleEnd);
        Assert.False((await _db.Subscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync()).CancelAtPeriodEnd);
    }

    [Fact]
    public async Task Cancelling_twice_asks_razorpay_once()
    {
        var checkout = await ActiveMandateAsync();

        await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: true);
        await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: true);
        Assert.Single(_gateway.CancelCalls);

        // Scheduled to stop, then told to stop now: that is a real second request.
        await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: false);
        await _service.CancelAsync(_tenant.Id, checkout.Id, cancelAtCycleEnd: false);
        Assert.Equal(2, _gateway.CancelCalls.Count);
    }

    // ── What the screen shows ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RazorpaySubscriptionStatus.Created, 0, false, AutoPayDisplayStatus.PendingAuthorization)]
    [InlineData(RazorpaySubscriptionStatus.Authenticated, 0, false, AutoPayDisplayStatus.PaymentPending)]
    [InlineData(RazorpaySubscriptionStatus.Authenticated, 1, false, AutoPayDisplayStatus.Active)]
    [InlineData(RazorpaySubscriptionStatus.Active, 1, false, AutoPayDisplayStatus.Active)]
    [InlineData(RazorpaySubscriptionStatus.Active, 1, true, AutoPayDisplayStatus.CancellationScheduled)]
    [InlineData(RazorpaySubscriptionStatus.Authenticated, 0, true, AutoPayDisplayStatus.CancellationScheduled)]
    [InlineData(RazorpaySubscriptionStatus.Pending, 1, false, AutoPayDisplayStatus.PastDue)]
    [InlineData(RazorpaySubscriptionStatus.Halted, 1, false, AutoPayDisplayStatus.Halted)]
    [InlineData(RazorpaySubscriptionStatus.Paused, 1, false, AutoPayDisplayStatus.Paused)]
    [InlineData(RazorpaySubscriptionStatus.Cancelled, 1, true, AutoPayDisplayStatus.Cancelled)]
    [InlineData(RazorpaySubscriptionStatus.Completed, 120, false, AutoPayDisplayStatus.Expired)]
    [InlineData(RazorpaySubscriptionStatus.Expired, 0, false, AutoPayDisplayStatus.Expired)]
    public void The_status_shown_follows_razorpays_state(RazorpaySubscriptionStatus status, int paidCount, bool cancelAtCycleEnd, string expected)
    {
        var row = new RazorpaySubscription { Status = status, PaidCount = paidCount, CancelAtCycleEnd = cancelAtCycleEnd };

        Assert.Equal(expected, AutoPayService.DisplayStatus(row));
    }

    [Theory]
    [InlineData("created", RazorpaySubscriptionStatus.Created)]
    [InlineData("authenticated", RazorpaySubscriptionStatus.Authenticated)]
    [InlineData("active", RazorpaySubscriptionStatus.Active)]
    [InlineData("pending", RazorpaySubscriptionStatus.Pending)]
    [InlineData("halted", RazorpaySubscriptionStatus.Halted)]
    [InlineData("cancelled", RazorpaySubscriptionStatus.Cancelled)]
    [InlineData("completed", RazorpaySubscriptionStatus.Completed)]
    [InlineData("expired", RazorpaySubscriptionStatus.Expired)]
    [InlineData("paused", RazorpaySubscriptionStatus.Paused)]
    [InlineData(" Active ", RazorpaySubscriptionStatus.Active)]
    public void Every_razorpay_state_is_understood(string razorpay, RazorpaySubscriptionStatus expected)
    {
        Assert.Equal(expected, AutoPayService.ParseStatus(razorpay));
    }

    [Theory]
    [InlineData("resumed")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_guessed_at(string? razorpay)
    {
        Assert.Null(AutoPayService.ParseStatus(razorpay));
    }

    // ── Availability ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Availability_says_why_a_tenant_cannot_use_autopay()
    {
        Assert.Equal((true, "test"), ((await _service.GetAvailabilityAsync(_tenant.Id)).Available, (await _service.GetAvailabilityAsync(_tenant.Id)).Mode));

        var british = await _service.GetAvailabilityAsync(_british.Id);
        Assert.False(british.Available);
        Assert.Contains("rupees", british.Reason);

        _store.Rows[AutoPayService.EnabledKey] = "false";
        Assert.False((await _service.GetAvailabilityAsync(_tenant.Id)).Available);
    }

    [Fact]
    public async Task Autopay_is_required_only_for_rupees_with_razorpay_set_up_and_switched_on()
    {
        Assert.True(await _service.IsRequiredAsync("INR"));
        Assert.False(await _service.IsRequiredAsync("GBP"));

        _store.Rows.Remove(RazorpayService.KeyIdKey);
        Assert.False(await _service.IsRequiredAsync("INR"));
    }

    // ── The simulated purchase stands aside ─────────────────────────────────────────────

    [Fact]
    public async Task With_autopay_on_a_paid_plan_can_no_longer_be_had_without_paying()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _billing.ChoosePlanAsync(_tenant.Id, _premium.Id));

        // Refused before anything was changed - and nothing sneaks in on the next save either.
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        Assert.False(await _db.Subscriptions.IgnoreQueryFilters().AnyAsync());
        Assert.False(await _db.Payments.IgnoreQueryFilters().AnyAsync());
        Assert.False(await _db.QuotaLedgerEntries.IgnoreQueryFilters().AnyAsync());
        Assert.Equal(TenantStatus.Trial, (await _db.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenant.Id)).Status);
    }

    [Fact]
    public async Task With_autopay_on_a_free_plan_is_still_chosen_directly()
    {
        var subscription = await _billing.ChoosePlanAsync(_tenant.Id, _trial.Id);

        Assert.Equal(_trial.Id, subscription.PlanId);
    }

    [Fact]
    public async Task With_autopay_off_choosing_a_paid_plan_works_as_it_always_did()
    {
        _store.Rows[AutoPayService.EnabledKey] = "false";

        var subscription = await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);

        Assert.Equal(_premium.Id, subscription.PlanId);
        Assert.Equal("Simulated", (await _db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Provider);
    }

    [Fact]
    public async Task A_tenant_billed_in_another_currency_still_chooses_a_paid_plan_directly()
    {
        var subscription = await _billing.ChoosePlanAsync(_british.Id, _premium.Id);

        Assert.Equal(_premium.Id, subscription.PlanId);
    }
}
