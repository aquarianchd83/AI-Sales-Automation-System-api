using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The single wallet end to end through the router every spend goes through: what an action costs, the features a plan includes, the monthly cap a tenant puts
/// on a feature, and each feature's own ledger.</summary>
public sealed class CreditWalletTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Clock _clock = new() { UtcNow = Now };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly CreditRouter _router;
    private readonly CreditWalletService _wallet;
    private readonly CreditPayerService _payers;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Plan _trial = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Trial", StarterCredits = 50m, IsActive = true };
    private readonly Plan _premium = new() { Code = PlatformFeatureCatalog.PremiumPlanCode, Name = "Premium", MonthlyCredits = 100m, IsActive = true };

    public CreditWalletTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        var costs = new CreditCostService(_db);
        var access = new FeatureAccessService(_db);
        _wallet = new CreditWalletService(_db, _ledger, access, costs, _clock);
        _router = new CreditRouter(_db, _ledger, _clock, NullLogger<CreditRouter>.Instance, costs, access, _wallet);
        _payers = new CreditPayerService(_db, _clock, _ledger);

        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Acme", Slug = "acme" });
        _db.Plans.AddRange(_trial, _premium);
        foreach (var feature in PlatformFeatureCatalog.TrialFeatures)
            _db.PlanFeatures.Add(new PlanFeature { PlanId = _trial.Id, Feature = feature });
        foreach (var feature in PlatformFeatureCatalog.PremiumFeatures)
            _db.PlanFeatures.Add(new PlanFeature { PlanId = _premium.Id, Feature = feature });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task OnPlanAsync(Plan plan, decimal credits = 100m)
    {
        _db.Subscriptions.Add(new Subscription { TenantId = _tenant, PlanId = plan.Id, Status = SubscriptionStatus.Active });
        await _db.SaveChangesAsync();
        await _ledger.GrantPlanCreditsAsync(_tenant, credits, "test:credits", "test credits", QuotaGrantOrigin.PlanAllocation);
    }

    private Task<ConsumeResult> SendAsync(string key, decimal units = 1m) =>
        _router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.Credits, units, key, "Message", key));

    private Task<ConsumeResult> ReplyAsync(string key) =>
        _router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.Credits, 1m, key, "AiTransaction", key));

    private Task<ConsumeResult> DiscoverAsync(string key, decimal candidates, bool partial = false) =>
        _router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.Credits, candidates, key, "LeadDiscoveryRun", key, AllowPartial: partial));

    private async Task<decimal> BalanceAsync() => (await _ledger.GetBalancesAsync(_tenant)).Single().Balance;

    private Task SetCostAsync(CreditScenario scenario, decimal credits) =>
        _payers.SetPayerAsync(scenario, new UpdateCreditPayerRequest(CreditPayer.Tenant, credits), Guid.NewGuid());

    // ---- what an action costs

    [Fact]
    public async Task An_action_costs_the_credits_the_platform_set_for_each_unit_and_the_caller_hears_in_its_own_units()
    {
        await OnPlanAsync(_premium);
        await SetCostAsync(CreditScenario.WhatsAppTemplateSend, 3m);

        var result = await SendAsync("m1", units: 2m);

        Assert.True(result.Sufficient);
        Assert.Equal(2m, result.Consumed); // two message units, as asked
        Assert.Equal(6m, result.CreditsSpent); // at 3 credits each
        Assert.Equal(94m, await BalanceAsync());
    }

    [Fact]
    public async Task Every_scenario_has_a_default_price()
    {
        var all = await new CreditCostService(_db).GetAllAsync();

        Assert.Equal(CreditScenarioCatalog.All.Count, all.Count);
        Assert.All(all.Values, credits => Assert.True(credits > 0));
    }

    // ---- what a plan includes

    [Fact]
    public async Task A_feature_outside_the_plan_spends_nothing()
    {
        await OnPlanAsync(_trial);

        var result = await DiscoverAsync("lead-1", 5m); // lead discovery is Premium

        Assert.False(result.Sufficient);
        Assert.Equal(0m, result.Consumed);
        Assert.Equal(100m, await BalanceAsync());
    }

    [Fact]
    public async Task A_feature_inside_the_plan_spends_and_is_tagged_in_its_own_ledger()
    {
        await OnPlanAsync(_trial);

        await SendAsync("m1");
        await ReplyAsync("a1");

        var messaging = await _wallet.GetFeatureLedgerAsync(_tenant, PlatformFeature.WhatsAppMessaging, new PagedRequest { Page = 1, PageSize = 20 });
        var ai = await _wallet.GetFeatureLedgerAsync(_tenant, PlatformFeature.AiConversations, new PagedRequest { Page = 1, PageSize = 20 });
        Assert.Equal(-1m, Assert.Single(messaging.Items).UnitsDelta);
        Assert.Equal(-1m, Assert.Single(ai.Items).UnitsDelta);
    }

    [Fact]
    public async Task A_tenant_with_no_plan_row_is_on_the_trial_plan_not_on_nothing()
    {
        await _ledger.GrantPlanCreditsAsync(_tenant, 10m, "test:credits", "test credits", QuotaGrantOrigin.PlanAllocation);

        Assert.True((await SendAsync("m1")).Sufficient);
        Assert.False((await DiscoverAsync("lead-1", 1m)).Sufficient);
    }

    // ---- the tenant's own monthly cap

    [Fact]
    public async Task A_monthly_cap_stops_a_feature_even_when_the_wallet_has_credits()
    {
        await OnPlanAsync(_premium);
        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.WhatsAppMessaging, 3m));

        Assert.True((await SendAsync("m1", 2m)).Sufficient);
        Assert.False((await SendAsync("m2", 2m)).Sufficient); // would take it to 4 of 3
        Assert.True((await SendAsync("m3", 1m)).Sufficient); // exactly the cap
        Assert.False((await SendAsync("m4", 1m)).Sufficient);

        Assert.Equal(97m, await BalanceAsync());
        Assert.True((await ReplyAsync("a1")).Sufficient); // another feature is not touched by it
    }

    [Fact]
    public async Task The_cap_resets_each_month()
    {
        await OnPlanAsync(_premium);
        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.WhatsAppMessaging, 1m));
        await SendAsync("m1");
        Assert.False((await SendAsync("m2")).Sufficient);

        _clock.UtcNow = Now.AddMonths(1);

        Assert.True((await SendAsync("m3")).Sufficient);
    }

    [Fact]
    public async Task A_spend_that_can_be_partial_takes_what_the_cap_allows()
    {
        await OnPlanAsync(_premium);
        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.LeadDiscovery, 4m));

        var result = await DiscoverAsync("lead-1", 10m, partial: true);

        Assert.False(result.Sufficient);
        Assert.Equal(4m, result.Consumed);
        Assert.Equal(96m, await BalanceAsync());
        Assert.Equal(0m, (await _wallet.GetCapRemainingAsync(_tenant, PlatformFeature.LeadDiscovery)));
    }

    [Fact]
    public async Task Giving_a_spend_back_gives_the_cap_back_too()
    {
        await OnPlanAsync(_premium);
        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.WhatsAppMessaging, 1m));
        await SendAsync("m1");

        await _router.ReverseAsync(_tenant, "m1");

        Assert.Equal(1m, await _wallet.GetCapRemainingAsync(_tenant, PlatformFeature.WhatsAppMessaging));
        Assert.Equal(100m, await BalanceAsync());
    }

    [Fact]
    public async Task Affordable_units_respect_the_wallet_the_cap_the_price_and_the_plan()
    {
        await OnPlanAsync(_premium);
        await SetCostAsync(CreditScenario.LeadDiscovery, 2m);
        Assert.Equal(50m, await _wallet.GetAffordableUnitsAsync(_tenant, CreditScenario.LeadDiscovery)); // 100 credits at 2 each

        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.LeadDiscovery, 9m));
        Assert.Equal(4m, await _wallet.GetAffordableUnitsAsync(_tenant, CreditScenario.LeadDiscovery)); // 9 credits of cap at 2 each, whole units only

        var trialTenant = Guid.NewGuid();
        _db.Tenants.Add(new Tenant { Id = trialTenant, Name = "Other", Slug = "other" });
        await _db.SaveChangesAsync();
        Assert.Equal(0m, await _wallet.GetAffordableUnitsAsync(trialTenant, CreditScenario.LeadDiscovery)); // not in its (trial) plan
    }

    [Fact]
    public async Task A_used_up_monthly_limit_is_reported_as_that_and_not_as_missing_credits()
    {
        await OnPlanAsync(_premium);
        await SetCostAsync(CreditScenario.LeadDiscovery, 1m);
        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.LeadDiscovery, 4m));
        await DiscoverAsync("lead-1", 4m, partial: false);

        var shortfall = await _wallet.GetShortfallAsync(_tenant, CreditScenario.LeadDiscovery);

        Assert.Equal(0m, await _wallet.GetAffordableUnitsAsync(_tenant, CreditScenario.LeadDiscovery));
        Assert.True(await BalanceAsync() > 0m); // the wallet is not the problem
        Assert.NotNull(shortfall);
        Assert.Equal(QuotaShortfallKind.MonthlyLimitReached, shortfall!.Kind);
        Assert.Contains("monthly limit of 4", shortfall.Message);
        Assert.Contains("still has", shortfall.Message);
    }

    [Fact]
    public async Task An_empty_wallet_is_reported_as_missing_credits_and_a_plan_without_the_feature_as_that()
    {
        await OnPlanAsync(_premium);
        await SetCostAsync(CreditScenario.LeadDiscovery, 1000m); // more than the wallet holds

        Assert.Equal(QuotaShortfallKind.InsufficientCredits, (await _wallet.GetShortfallAsync(_tenant, CreditScenario.LeadDiscovery))!.Kind);

        var trialTenant = Guid.NewGuid();
        _db.Tenants.Add(new Tenant { Id = trialTenant, Name = "Other", Slug = "other" });
        await _db.SaveChangesAsync();
        Assert.Equal(QuotaShortfallKind.NotInPlan, (await _wallet.GetShortfallAsync(trialTenant, CreditScenario.LeadDiscovery))!.Kind);
    }

    [Fact]
    public async Task Nothing_is_reported_when_the_tenant_can_afford_it()
    {
        await OnPlanAsync(_premium);
        await SetCostAsync(CreditScenario.LeadDiscovery, 1m);

        Assert.Null(await _wallet.GetShortfallAsync(_tenant, CreditScenario.LeadDiscovery));
    }

    // ---- setting a cap

    [Fact]
    public async Task A_cap_can_be_set_changed_and_removed()
    {
        await OnPlanAsync(_premium);

        var set = await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.ContentStudio, 20m));
        Assert.Equal(20m, set.Features.Single(f => f.Feature == PlatformFeature.ContentStudio).MonthlyCap);

        var changed = await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.ContentStudio, 5m));
        Assert.Equal(5m, changed.Features.Single(f => f.Feature == PlatformFeature.ContentStudio).MonthlyCap);

        var removed = await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.ContentStudio, null));
        Assert.Null(removed.Features.Single(f => f.Feature == PlatformFeature.ContentStudio).MonthlyCap);
        Assert.Empty(await _db.TenantFeatureCaps.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_cap_cannot_be_negative_or_set_on_a_feature_outside_the_plan()
    {
        await OnPlanAsync(_trial);

        await Assert.ThrowsAsync<ValidationException>(() => _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.WhatsAppMessaging, -1m)));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.ContentStudio, 5m)));
    }

    // ---- what the tenant sees

    [Fact]
    public async Task The_summary_shows_the_balance_what_each_feature_spent_this_month_its_cap_and_its_costs()
    {
        await OnPlanAsync(_premium);
        await _wallet.SetCapAsync(_tenant, new SetFeatureCapRequest(PlatformFeature.WhatsAppMessaging, 10m));
        await SendAsync("m1", 2m);
        await ReplyAsync("a1");

        var summary = await _wallet.GetSummaryAsync(_tenant);

        Assert.Equal(97m, summary.Balance);
        Assert.Equal("premium", summary.PlanCode);
        Assert.Equal(100m, summary.MonthlyCredits);
        var messaging = summary.Features.Single(f => f.Feature == PlatformFeature.WhatsAppMessaging);
        Assert.Equal((2m, 10m, true), (messaging.SpentThisMonth, messaging.MonthlyCap, messaging.IncludedInPlan));
        var cost = Assert.Single(messaging.Costs);
        Assert.Equal((CreditScenario.WhatsAppTemplateSend, 1m), (cost.Scenario, cost.Credits));
        Assert.Equal(1m, summary.Features.Single(f => f.Feature == PlatformFeature.AiConversations).SpentThisMonth);
    }

    [Fact]
    public async Task Entitlements_list_only_the_plans_features_with_their_costs()
    {
        await OnPlanAsync(_trial);
        await SetCostAsync(CreditScenario.CustomerChatReply, 2m);

        var entitlements = await _wallet.GetEntitlementsAsync(_tenant);

        Assert.Equal(new[] { PlatformFeature.WhatsAppMessaging, PlatformFeature.AiConversations }, entitlements.Features.ToArray());
        Assert.Equal(2m, entitlements.Costs.Single(c => c.Scenario == CreditScenario.CustomerChatReply).Credits);
        Assert.DoesNotContain(entitlements.Costs, c => c.Feature == PlatformFeature.ContentStudio);
        Assert.Equal(100m, entitlements.Balance);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }
}
