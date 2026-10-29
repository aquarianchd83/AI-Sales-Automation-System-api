using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The flows that put credits in a tenant's wallet, against the real ledger: going onto Trial or Premium (by paying or by an operator), the monthly
/// credits, and buying a pack. Plans never expire, so none of these involves a billing period.</summary>
public sealed class PlanAndCreditFlowTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new() { UtcNow = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc) };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly CreditAllocationService _credits;
    private readonly BillingService _billing;
    private readonly PlatformTenantService _tenants;
    private readonly Tenant _tenant = new() { Name = "Confianza", Slug = "confianza", CountryCode = "IN", Status = TenantStatus.Trial };
    private readonly Plan _trial = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Trial", StarterCredits = 50m, IsActive = true };
    private readonly Plan _premium = new() { Code = PlatformFeatureCatalog.PremiumPlanCode, Name = "Premium", PriceMonthlyCents = 2900, MonthlyCredits = 100m, IsActive = true };
    private readonly CreditPack _pack = new() { QuotaType = QuotaType.Credits, Name = "1,000 credits", Units = 1000, PriceCents = 2000 };

    public PlanAndCreditFlowTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        _credits = new CreditAllocationService(_db, _ledger, _clock);
        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        _billing = new BillingService(_db, new PlatformContext(), _clock, jobs, _ledger, TestPricing.NoTax(), _credits);

        var audit = Fake.Of<IPlatformAuditService>((m, _) => m.Name == nameof(IPlatformAuditService.LogAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        // Only what OverridePlanAsync touches is real; the rest of the service is not exercised here.
        _tenants = new PlatformTenantService(_db, null!, null!, null!, _clock, null!, null!, null!, null!, null!, audit, null!, _credits, null!, null!, null!);

        _db.Tenants.Add(_tenant);
        _db.Plans.AddRange(_trial, _premium);
        foreach (var feature in PlatformFeatureCatalog.TrialFeatures)
            _db.PlanFeatures.Add(new PlanFeature { PlanId = _trial.Id, Feature = feature });
        foreach (var feature in PlatformFeatureCatalog.PremiumFeatures)
            _db.PlanFeatures.Add(new PlanFeature { PlanId = _premium.Id, Feature = feature });
        _db.CreditPacks.Add(_pack);
        // Prices are per country; the test tenant is in India (the USD cents above x 83).
        _db.PlanPrices.Add(new PlanPrice { PlanId = _premium.Id, CountryCode = "IN", Amount = 2407m });
        _db.CreditPackPrices.Add(new CreditPackPrice { CreditPackId = _pack.Id, CountryCode = "IN", Amount = 1660m });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<decimal> BalanceAsync() =>
        (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == QuotaType.Credits).Balance;

    private Task<Subscription> SubscriptionAsync() => _db.Subscriptions.IgnoreQueryFilters().SingleAsync();

    // ---- The Trial plan

    [Fact]
    public async Task A_new_tenant_on_the_trial_plan_gets_its_starter_credits_once_and_they_stay()
    {
        await _credits.AssignTrialPlanAsync(_tenant.Id);
        await _credits.AssignTrialPlanAsync(_tenant.Id);

        Assert.Equal(50m, await BalanceAsync());
        Assert.Equal(_trial.Id, (await SubscriptionAsync()).PlanId);

        _clock.UtcNow = _clock.UtcNow.AddYears(3);
        Assert.Equal(50m, await BalanceAsync()); // the plan never expires, so neither do these
    }

    [Fact]
    public async Task The_trial_plan_gets_no_monthly_credits()
    {
        await _credits.AssignTrialPlanAsync(_tenant.Id);

        Assert.Equal(0, await _credits.AllocateMonthlyAsync());
        Assert.Equal(50m, await BalanceAsync());
    }

    // ---- Choosing Premium

    [Fact]
    public async Task Choosing_premium_charges_it_and_gives_this_months_credits_with_no_billing_period()
    {
        var subscription = await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);

        Assert.Equal("Active", subscription.Status);
        Assert.Null(subscription.CurrentPeriodEndUtc);
        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(TenantStatus.Active, (await _db.Tenants.IgnoreQueryFilters().SingleAsync()).Status);

        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentKind.Subscription, payment.Kind);
        Assert.Equal(2900, payment.AmountCents);
    }

    [Fact]
    public async Task Going_from_trial_to_premium_keeps_the_starter_credits_and_adds_the_months()
    {
        await _credits.AssignTrialPlanAsync(_tenant.Id);

        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);

        Assert.Equal(150m, await BalanceAsync()); // 50 starter + 100 for the month
    }

    // ---- Going back to a free plan

    [Fact]
    public async Task A_free_plan_is_sold_only_when_it_is_priced_at_zero_on_purpose()
    {
        // No price row for the country: not sold, exactly as before.
        await Assert.ThrowsAsync<ConflictException>(() => _billing.ChoosePlanAsync(_tenant.Id, _trial.Id));
        Assert.DoesNotContain(await _billing.GetPlansAsync("IN"), p => p.Id == _trial.Id);

        _db.PlanPrices.Add(new PlanPrice { PlanId = _trial.Id, CountryCode = "IN", Amount = 0m });
        await _db.SaveChangesAsync();

        Assert.Contains(await _billing.GetPlansAsync("IN"), p => p.Id == _trial.Id && p.LocalPriceAmount == 0m);
    }

    [Fact]
    public async Task Downgrading_to_a_free_plan_charges_nothing_and_never_hands_out_the_starter_credits_again()
    {
        _db.PlanPrices.Add(new PlanPrice { PlanId = _trial.Id, CountryCode = "IN", Amount = 0m });
        await _db.SaveChangesAsync();
        await _credits.AssignTrialPlanAsync(_tenant.Id);
        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);
        Assert.Equal(150m, await BalanceAsync()); // 50 starter + 100 for the month

        var subscription = await _billing.ChoosePlanAsync(_tenant.Id, _trial.Id);

        Assert.Equal(_trial.Id, subscription.PlanId);
        Assert.Equal(150m, await BalanceAsync()); // what they have is kept, and no second 50
        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync(); // Premium's; the free plan recorded none
        Assert.Equal(PaymentKind.Subscription, payment.Kind);
        Assert.Equal(_premium.Id, payment.PlanId);

        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);
        await _billing.ChoosePlanAsync(_tenant.Id, _trial.Id);
        Assert.Equal(150m, await BalanceAsync());
    }

    [Fact]
    public async Task Premium_gets_new_credits_each_month_once_and_unspent_ones_carry_over()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);
        await _ledger.ConsumeAsync(new ConsumeRequest(_tenant.Id, QuotaType.Credits, 30m, "spend", "Message", "spend"));
        Assert.Equal(70m, await BalanceAsync());

        Assert.Equal(0, await _credits.AllocateMonthlyAsync()); // the same month again: nothing
        _clock.UtcNow = _clock.UtcNow.AddMonths(1);
        Assert.Equal(1, await _credits.AllocateMonthlyAsync());
        Assert.Equal(0, await _credits.AllocateMonthlyAsync());

        Assert.Equal(170m, await BalanceAsync()); // 70 carried over + 100 new
    }

    [Fact]
    public async Task Premium_includes_every_feature_and_trial_only_the_minimum()
    {
        var access = new FeatureAccessService(_db);

        await _credits.AssignTrialPlanAsync(_tenant.Id);
        Assert.True(await access.HasAsync(_tenant.Id, PlatformFeature.WhatsAppMessaging));
        Assert.False(await access.HasAsync(_tenant.Id, PlatformFeature.ContentStudio));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => access.RequireAsync(_tenant.Id, PlatformFeature.LeadDiscovery));

        await _credits.AssignPlanAsync(_tenant.Id, _premium.Id);
        var premiumAccess = new FeatureAccessService(_db);
        foreach (var feature in Enum.GetValues<PlatformFeature>())
            Assert.True(await premiumAccess.HasAsync(_tenant.Id, feature));
    }

    // ---- Buying credits

    [Fact]
    public async Task Buying_credits_tells_the_tenant_they_were_added()
    {
        var notifier = new CapturingNotifier();
        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        var billing = new BillingService(_db, new PlatformContext(), _clock, jobs, _ledger, TestPricing.NoTax(), _credits, notifier);
        await billing.ChoosePlanAsync(_tenant.Id, _premium.Id);

        var payment = await billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);

        var sent = Assert.Single(notifier.Requests);
        Assert.Equal(TenantNotificationKind.CreditsAdded, sent.Kind);
        Assert.Equal(_tenant.Id, sent.TenantId);
        Assert.Equal(QuotaType.Credits, sent.QuotaType);
        Assert.Equal(payment.Id.ToString("N"), sent.EpisodeKey);
        Assert.Contains("1,000", sent.Title);
    }

    private sealed class CapturingNotifier : ITenantNotifier
    {
        public List<TenantNotificationRequest> Requests { get; } = new();

        public Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Credits_cannot_be_bought_without_a_plan()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id));

        Assert.Empty(await _db.Payments.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task Credits_cannot_be_bought_while_suspended_or_after_the_subscription_is_cancelled()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);

        _tenant.Status = TenantStatus.Suspended;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id));

        _tenant.Status = TenantStatus.Active;
        (await SubscriptionAsync()).Status = SubscriptionStatus.Canceled;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id));
    }

    [Fact]
    public async Task A_purchase_records_the_payment_in_the_tenants_currency_and_adds_credits_for_twelve_months()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);
        _clock.UtcNow = _clock.UtcNow.AddDays(3);

        var payment = await _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);

        Assert.Equal("CreditPack", payment.Kind);
        Assert.Equal("INR", payment.CurrencyCode);
        Assert.Equal(1660m, payment.LocalAmount);
        Assert.Equal(1100m, await BalanceAsync()); // the month's 100 + the pack's 1,000

        var credits = (await _ledger.GetBalancesAsync(_tenant.Id)).Single().Grants.Single(g => g.Origin == QuotaGrantOrigin.CreditPurchase);
        Assert.Equal(_clock.UtcNow.AddMonths(12), credits.ExpiresAtUtc);
    }

    [Fact]
    public async Task Credits_can_be_bought_after_the_plans_credits_are_used_up()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _premium.Id);
        await _ledger.ConsumeAsync(new ConsumeRequest(_tenant.Id, QuotaType.Credits, 100, "all", "Message", "all"));
        Assert.Equal(0m, await BalanceAsync());

        await _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);

        Assert.Equal(1000m, await BalanceAsync());
    }

    // ---- An operator overriding a tenant's plan

    private Task OverrideAsync(Plan plan) =>
        _tenants.OverridePlanAsync(_tenant.Id, new OverrideTenantPlanRequest(plan.Id), Guid.NewGuid(), "ops@example.com");

    [Fact]
    public async Task An_override_puts_the_tenant_on_the_plan_and_gives_its_credits_with_no_period_and_no_payment()
    {
        await OverrideAsync(_premium);

        var subscription = await SubscriptionAsync();
        Assert.Equal(_premium.Id, subscription.PlanId);
        Assert.Null(subscription.CurrentPeriodEndUtc);
        Assert.Equal(100m, await BalanceAsync());
        Assert.Null((await _db.QuotaGrants.IgnoreQueryFilters().SingleAsync()).PaymentId); // nothing was paid - the operator chose it
    }

    [Fact]
    public async Task Overriding_back_to_trial_takes_nothing_away_and_does_not_repeat_the_starter_credits()
    {
        await OverrideAsync(_trial);
        await OverrideAsync(_premium);
        await OverrideAsync(_trial);

        Assert.Equal(_trial.Id, (await SubscriptionAsync()).PlanId);
        Assert.Equal(150m, await BalanceAsync()); // 50 starter (once) + 100 for the month, nothing forfeited
    }

    [Fact]
    public async Task Overriding_to_the_plan_the_tenant_already_has_changes_nothing()
    {
        await OverrideAsync(_premium);
        await OverrideAsync(_premium);

        Assert.Equal(100m, await BalanceAsync());
        Assert.Equal(1, await _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.EntryType == QuotaEntryType.Allocation));
    }
}
