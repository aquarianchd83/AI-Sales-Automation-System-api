using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The money flows that put quota in a tenant's hands: choosing a plan, an operator overriding it, and
/// buying credits. Each is checked end to end against the real ledger.</summary>
public sealed class PlanAndCreditFlowTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly BillingService _billing;
    private readonly PlatformTenantService _tenants;
    private readonly Tenant _tenant = new() { Name = "Confianza", Slug = "confianza", CountryCode = "IN", Status = TenantStatus.Active };
    private readonly Plan _starter = new() { Code = "starter", Name = "Starter", PriceMonthlyCents = 3900 };
    private readonly Plan _growth = new() { Code = "growth", Name = "Growth", PriceMonthlyCents = 9900 };
    private readonly CreditPack _pack = new() { QuotaType = QuotaType.WhatsAppMessages, Name = "1,000 WhatsApp messages", Units = 1000, PriceCents = 2000 };

    public PlanAndCreditFlowTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        _billing = new BillingService(_db, new PlatformContext(), _clock, jobs, _ledger, TestPricing.NoTax());

        var audit = Fake.Of<IPlatformAuditService>((m, _) => m.Name == nameof(IPlatformAuditService.LogAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        // Only what OverridePlanAsync touches is real; the rest of the service is not exercised here.
        _tenants = new PlatformTenantService(_db, null!, null!, null!, _clock, null!, null!, null!, null!, null!, audit, null!, _ledger, null!, null!);

        _db.Tenants.Add(_tenant);
        _db.Plans.AddRange(_starter, _growth);
        _db.PlanQuotas.Add(new PlanQuota { PlanId = _starter.Id, QuotaType = QuotaType.WhatsAppMessages, IncludedUnits = 500 });
        _db.PlanQuotas.Add(new PlanQuota { PlanId = _starter.Id, QuotaType = QuotaType.AiConversations, IncludedUnits = 500 });
        _db.PlanQuotas.Add(new PlanQuota { PlanId = _growth.Id, QuotaType = QuotaType.WhatsAppMessages, IncludedUnits = 3000 });
        _db.CreditPacks.Add(_pack);
        // Prices are per country; the test tenant is in India (the USD cents above x 83).
        _db.PlanPrices.Add(new PlanPrice { PlanId = _starter.Id, CountryCode = "IN", Amount = 3237m });
        _db.PlanPrices.Add(new PlanPrice { PlanId = _growth.Id, CountryCode = "IN", Amount = 8217m });
        _db.CreditPackPrices.Add(new CreditPackPrice { CreditPackId = _pack.Id, CountryCode = "IN", Amount = 1660m });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<decimal> BalanceAsync(QuotaType type = QuotaType.WhatsAppMessages) =>
        (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == type).Balance;

    // ---- Choosing a plan

    [Fact]
    public async Task Choosing_a_plan_charges_it_and_hands_over_its_included_quota()
    {
        var subscription = await _billing.ChoosePlanAsync(_tenant.Id, _starter.Id);

        Assert.Equal("Active", subscription.Status);
        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(500m, await BalanceAsync(QuotaType.AiConversations));
        Assert.Equal(0m, await BalanceAsync(QuotaType.LeadCandidates));

        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentKind.Subscription, payment.Kind);
        Assert.Equal(3900, payment.AmountCents);
    }

    [Fact]
    public async Task Switching_plan_replaces_the_old_included_quota_and_keeps_bought_credits()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _starter.Id);
        await _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);
        Assert.Equal(1500m, await BalanceAsync());

        await _billing.ChoosePlanAsync(_tenant.Id, _growth.Id);

        Assert.Equal(4000m, await BalanceAsync()); // 3,000 from Growth + the 1,000 credits, not 500 + 3,000 + 1,000
        Assert.Equal(0m, await BalanceAsync(QuotaType.AiConversations)); // Growth defines no AI quota here
    }

    // ---- Buying credits

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
        await _billing.ChoosePlanAsync(_tenant.Id, _starter.Id);

        _tenant.Status = TenantStatus.Suspended;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id));

        _tenant.Status = TenantStatus.Active;
        (await _db.Subscriptions.IgnoreQueryFilters().SingleAsync()).Status = SubscriptionStatus.Canceled;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id));
    }

    [Fact]
    public async Task A_purchase_records_the_payment_in_the_tenants_currency_and_adds_credits_for_twelve_months()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _starter.Id);
        _clock.UtcNow = _clock.UtcNow.AddDays(3);

        var payment = await _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);

        Assert.Equal("CreditPack", payment.Kind);
        Assert.Equal("INR", payment.CurrencyCode);
        Assert.Equal(1660m, payment.LocalAmount);
        Assert.Equal(1500m, await BalanceAsync());

        var credits = (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == QuotaType.WhatsAppMessages)
            .Grants.Single(g => g.Origin == QuotaGrantOrigin.CreditPurchase);
        Assert.Equal(_clock.UtcNow.AddMonths(12), credits.ExpiresAtUtc);
    }

    [Fact]
    public async Task Credits_can_be_bought_after_the_included_quota_is_used_up()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _starter.Id);
        await _ledger.ConsumeAsync(new ConsumeRequest(_tenant.Id, QuotaType.WhatsAppMessages, 500, "all", "Message", "all"));
        Assert.Equal(0m, await BalanceAsync());

        await _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);

        Assert.Equal(1000m, await BalanceAsync());
    }

    // ---- An operator overriding a tenant's plan

    private Task OverrideAsync(Plan plan) =>
        _tenants.OverridePlanAsync(_tenant.Id, new OverrideTenantPlanRequest(plan.Id), Guid.NewGuid(), "ops@example.com");

    [Fact]
    public async Task An_override_gives_the_tenant_the_plans_quota_not_just_the_plan()
    {
        await OverrideAsync(_starter);

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_starter.Id, subscription.PlanId);
        Assert.Equal(_clock.UtcNow, subscription.CurrentPeriodStartUtc);
        Assert.Equal(_clock.UtcNow.AddMonths(1), subscription.CurrentPeriodEndUtc);
        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(500m, await BalanceAsync(QuotaType.AiConversations));

        var grant = await _db.QuotaGrants.IgnoreQueryFilters().FirstAsync(g => g.QuotaType == QuotaType.WhatsAppMessages);
        Assert.Equal(subscription.CurrentPeriodEndUtc, grant.ExpiresAtUtc);
        Assert.Null(grant.PaymentId); // nothing was paid - the operator chose it
    }

    [Fact]
    public async Task Overriding_to_another_plan_swaps_the_quota_without_restarting_the_running_period()
    {
        await _billing.ChoosePlanAsync(_tenant.Id, _starter.Id);
        var periodEnd = (await _db.Subscriptions.IgnoreQueryFilters().SingleAsync()).CurrentPeriodEndUtc;
        await _billing.PurchaseCreditPackAsync(_tenant.Id, _pack.Id);
        _clock.UtcNow = _clock.UtcNow.AddDays(5);

        await OverrideAsync(_growth);

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_growth.Id, subscription.PlanId);
        Assert.Equal(periodEnd, subscription.CurrentPeriodEndUtc);
        Assert.Equal(4000m, await BalanceAsync()); // Growth's 3,000 + the bought 1,000; Starter's 500 forfeited
    }

    [Fact]
    public async Task Overriding_to_the_plan_the_tenant_already_has_changes_nothing()
    {
        await OverrideAsync(_starter);
        await OverrideAsync(_starter);

        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(2, await _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.EntryType == QuotaEntryType.Allocation));
    }

    [Fact]
    public async Task An_override_on_a_lapsed_period_starts_a_fresh_one_so_the_quota_is_actually_usable()
    {
        _db.Subscriptions.Add(new Subscription
        {
            TenantId = _tenant.Id, Status = SubscriptionStatus.Active,
            CurrentPeriodStartUtc = _clock.UtcNow.AddMonths(-2), CurrentPeriodEndUtc = _clock.UtcNow.AddMonths(-1)
        });
        await _db.SaveChangesAsync();

        await OverrideAsync(_starter);

        Assert.Equal(500m, await BalanceAsync());
        Assert.Equal(_clock.UtcNow.AddMonths(1), (await _db.Subscriptions.IgnoreQueryFilters().SingleAsync()).CurrentPeriodEndUtc);
    }
}
