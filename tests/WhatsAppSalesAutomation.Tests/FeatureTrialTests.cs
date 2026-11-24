using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Free uses of a feature a plan does not include: a Basic tenant can try lead discovery for a few leads, then is asked to upgrade.</summary>
public sealed class FeatureTrialTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new() { UtcNow = Now };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Plan _basic = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Basic", IsActive = true };
    private readonly Plan _premium = new() { Code = PlatformFeatureCatalog.PremiumPlanCode, Name = "Premium", IsActive = true };

    public FeatureTrialTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _ledger = new QuotaLedgerService(_db, _clock);

        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Acme", Slug = "acme" });
        _db.Plans.AddRange(_basic, _premium);
        foreach (var feature in PlatformFeatureCatalog.TrialFeatures)
            _db.PlanFeatures.Add(new PlanFeature { PlanId = _basic.Id, Feature = feature });
        foreach (var feature in PlatformFeatureCatalog.PremiumFeatures)
            _db.PlanFeatures.Add(new PlanFeature { PlanId = _premium.Id, Feature = feature });
        _db.PlanFeatureTrials.Add(new PlanFeatureTrial { PlanId = _basic.Id, Feature = PlatformFeature.LeadDiscovery, FreeUses = 3 });
        _db.Subscriptions.Add(new Subscription { TenantId = _tenant, PlanId = _basic.Id, Status = SubscriptionStatus.Active });
        _db.SaveChanges();
        _ledger.GrantPlanCreditsAsync(_tenant, 100m, "test:credits", "test credits", QuotaGrantOrigin.PlanAllocation).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // A fresh set of services is a fresh request: nothing is remembered between them but the database.
    private (FeatureAccessService Access, CreditWalletService Wallet, CreditRouter Router) Request()
    {
        var costs = new CreditCostService(_db);
        var access = new FeatureAccessService(_db, dateTime: _clock);
        var wallet = new CreditWalletService(_db, _ledger, access, costs, _clock);
        var router = new CreditRouter(_db, _ledger, _clock, NullLogger<CreditRouter>.Instance, costs, access, wallet);
        return (access, wallet, router);
    }

    private Task<ConsumeResult> ChargeLeadAsync(string key) =>
        Request().Router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.Credits, 1m, key, "LeadDiscoveryRun", key, AllowPartial: true));

    private async Task<int> UsedAsync() =>
        await _db.TenantFeatureTrialUsages.IgnoreQueryFilters().Where(u => u.TenantId == _tenant && u.Feature == PlatformFeature.LeadDiscovery)
            .Select(u => u.Used).FirstOrDefaultAsync();

    [Fact]
    public async Task A_basic_tenant_can_use_a_premium_feature_until_its_free_uses_are_gone_then_is_asked_to_upgrade()
    {
        Assert.True(await Request().Access.HasAsync(_tenant, PlatformFeature.LeadDiscovery));
        Assert.False(await Request().Access.HasAsync(_tenant, PlatformFeature.ContentStudio)); // no free uses given: locked as before

        for (var i = 0; i < 3; i++)
            Assert.True((await ChargeLeadAsync($"lead:{i}")).Sufficient);

        Assert.Equal(3, await UsedAsync());
        Assert.False(await Request().Access.HasAsync(_tenant, PlatformFeature.LeadDiscovery));
        Assert.DoesNotContain(PlatformFeature.LeadDiscovery, await Request().Access.GetFeaturesAsync(_tenant));

        var refused = await ChargeLeadAsync("lead:3");
        Assert.False(refused.Sufficient);
        Assert.Equal(0m, refused.Consumed);

        var ex = await Assert.ThrowsAsync<FeatureDisabledException>(() => Request().Access.RequireAsync(_tenant, PlatformFeature.LeadDiscovery));
        Assert.Contains("all 3 free leads", ex.Message);
        Assert.Contains("Upgrade to Premium", ex.Message);

        var shortfall = await Request().Wallet.GetShortfallAsync(_tenant, CreditScenario.LeadDiscovery);
        Assert.Equal(QuotaShortfallKind.NotInPlan, shortfall!.Kind);
        Assert.Contains("Basic plan", shortfall.Message);
    }

    [Fact]
    public async Task Free_uses_still_cost_credits_and_a_retried_charge_is_counted_once()
    {
        var balance = (await _ledger.GetBalancesAsync(_tenant)).Single().Balance;

        await ChargeLeadAsync("lead:a");
        await ChargeLeadAsync("lead:a"); // the same lead again: already charged

        Assert.Equal(1, await UsedAsync());
        Assert.True((await _ledger.GetBalancesAsync(_tenant)).Single().Balance < balance);
    }

    [Fact]
    public async Task The_ai_research_behind_a_run_is_not_counted_as_a_lead()
    {
        await Request().Router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.Credits, 1m, "pk:research", CreditScenarioCatalog.ProductKnowledgeReference, "pk:research",
            Scenario: CreditScenario.LeadDiscovery, UnitsAreCredits: true));

        Assert.Equal(0, await UsedAsync());
    }

    [Fact]
    public async Task A_run_is_sized_to_the_free_leads_left()
    {
        await ChargeLeadAsync("lead:a");

        Assert.Equal(2m, await Request().Wallet.GetAffordableUnitsAsync(_tenant, CreditScenario.LeadDiscovery));
    }

    [Fact]
    public async Task Entitlements_show_the_free_uses_left()
    {
        await ChargeLeadAsync("lead:a");

        var entitlements = await Request().Wallet.GetEntitlementsAsync(_tenant);

        Assert.Contains(PlatformFeature.LeadDiscovery, entitlements.Features);
        var trial = Assert.Single(entitlements.Trials!);
        Assert.Equal(PlatformFeature.LeadDiscovery, trial.Feature);
        Assert.Equal(3, trial.FreeUses);
        Assert.Equal(1, trial.Used);
        Assert.Equal(2, trial.Remaining);
        Assert.Equal("leads", trial.Unit);
    }

    [Fact]
    public async Task Premium_is_not_counted_and_a_tenant_coming_back_to_basic_keeps_what_it_used()
    {
        await ChargeLeadAsync("lead:a");

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        subscription.PlanId = _premium.Id;
        await _db.SaveChangesAsync();

        await ChargeLeadAsync("lead:b");
        await ChargeLeadAsync("lead:c");
        await ChargeLeadAsync("lead:d");
        Assert.Equal(1, await UsedAsync());
        Assert.Empty(await Request().Access.GetTrialsAsync(_tenant));

        subscription.PlanId = _basic.Id;
        await _db.SaveChangesAsync();
        Assert.Equal(2, (await Request().Access.GetTrialAsync(_tenant, PlatformFeature.LeadDiscovery))!.Remaining);
    }

    [Fact]
    public async Task Free_uses_set_to_zero_lock_the_feature_again()
    {
        var row = await _db.PlanFeatureTrials.SingleAsync();
        row.FreeUses = 0;
        await _db.SaveChangesAsync();

        Assert.False(await Request().Access.HasAsync(_tenant, PlatformFeature.LeadDiscovery));
        var ex = await Assert.ThrowsAsync<FeatureDisabledException>(() => Request().Access.RequireAsync(_tenant, PlatformFeature.LeadDiscovery));
        Assert.Contains("not part of your plan", ex.Message);
    }
}
