using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The start-up seeding: the two plans (Trial and Premium), their features and prices, the credit packs, retiring the plans from before the single wallet, and
/// putting every tenant on a plan with its credits - all safe to run on every start.</summary>
public sealed class PlanSeederTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new() { UtcNow = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc) };
    private readonly SqliteApplicationDbContext _db;
    private readonly ServiceProvider _services;

    public PlanSeederTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton<ApplicationDbContext>(_db);
        services.AddSingleton<ICreditAllocationService>(new CreditAllocationService(_db, new QuotaLedgerService(_db, _clock), _clock));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedAsync()
    {
        await PlanSeeder.SeedAsync(_services);
        await QuotaCatalogSeeder.SeedAsync(_services);
    }

    [Fact]
    public async Task Trial_is_the_minimum_and_premium_has_everything_and_the_monthly_credits()
    {
        await SeedAsync();

        var trial = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.TrialPlanCode);
        var premium = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.PremiumPlanCode);
        Assert.Equal((PlatformFeatureCatalog.TrialStarterCredits, 0m), (trial.StarterCredits, trial.MonthlyCredits));
        Assert.Equal((0m, PlatformFeatureCatalog.PremiumMonthlyCredits), (premium.StarterCredits, premium.MonthlyCredits));

        var trialFeatures = await _db.PlanFeatures.Where(f => f.PlanId == trial.Id).Select(f => f.Feature).ToListAsync();
        var premiumFeatures = await _db.PlanFeatures.Where(f => f.PlanId == premium.Id).Select(f => f.Feature).ToListAsync();
        Assert.Equal(PlatformFeatureCatalog.TrialFeatures.OrderBy(f => f), trialFeatures.OrderBy(f => f));
        Assert.Equal(Enum.GetValues<PlatformFeature>().Length, premiumFeatures.Count);
    }

    [Fact]
    public async Task Premium_is_priced_in_every_country_so_it_can_be_sold_there_and_trial_is_free()
    {
        await SeedAsync();

        var premium = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.PremiumPlanCode);
        Assert.Equal(RegionalPricingCatalog.All.Count, await _db.PlanPrices.CountAsync(p => p.PlanId == premium.Id));
        var trial = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.TrialPlanCode);
        Assert.Empty(await _db.PlanPrices.Where(p => p.PlanId == trial.Id).ToListAsync());
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing_and_never_overwrites_what_an_admin_edited()
    {
        await SeedAsync();
        var premium = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.PremiumPlanCode);
        premium.MonthlyCredits = 250m;
        premium.Name = "Premium Plus";
        
        await _db.SaveChangesAsync();

        await SeedAsync();

        var again = await _db.Plans.AsNoTracking().SingleAsync(p => p.Code == PlatformFeatureCatalog.PremiumPlanCode);
        Assert.Equal((250m, "Premium Plus"), (again.MonthlyCredits, again.Name));
        Assert.Equal(2, await _db.Plans.CountAsync(p => p.IsActive));
        Assert.Equal(3, await _db.CreditPacks.CountAsync(p => p.QuotaType == QuotaType.Credits));
    }

    [Fact]
    public async Task Plans_from_before_the_single_wallet_are_retired_and_their_tenants_moved()
    {
        var starter = new Plan { Code = "starter", Name = "Starter", IsActive = true };
        _db.Plans.Add(starter);
        var payer = new Tenant { Name = "Payer", Slug = "payer", Status = TenantStatus.Active };
        var trialist = new Tenant { Name = "Trialist", Slug = "trialist", Status = TenantStatus.Trial };
        var newcomer = new Tenant { Name = "Newcomer", Slug = "newcomer", Status = TenantStatus.Trial };
        _db.Tenants.AddRange(payer, trialist, newcomer);
        _db.Subscriptions.Add(new Subscription { TenantId = payer.Id, PlanId = starter.Id, Status = SubscriptionStatus.Active });
        _db.Subscriptions.Add(new Subscription { TenantId = trialist.Id, PlanId = starter.Id, Status = SubscriptionStatus.Trialing });
        await _db.SaveChangesAsync();

        await SeedAsync();

        Assert.False((await _db.Plans.SingleAsync(p => p.Code == "starter")).IsActive);
        var premium = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.PremiumPlanCode);
        var trial = await _db.Plans.SingleAsync(p => p.Code == PlatformFeatureCatalog.TrialPlanCode);
        var plans = await _db.Subscriptions.IgnoreQueryFilters().ToDictionaryAsync(s => s.TenantId, s => s.PlanId);
        Assert.Equal(premium.Id, plans[payer.Id]); // paying stays on the better plan
        Assert.Equal(trial.Id, plans[trialist.Id]);
        Assert.Equal(trial.Id, plans[newcomer.Id]); // a tenant with no plan row is put on Trial
    }

    [Fact]
    public async Task Every_tenant_ends_up_with_its_plans_credits_once_however_often_it_runs()
    {
        var tenant = new Tenant { Name = "New", Slug = "new", Status = TenantStatus.Trial };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        await SeedAsync();
        await SeedAsync();

        var balance = await new QuotaLedgerService(_db, _clock).GetBalancesAsync(tenant.Id);
        Assert.Equal(PlatformFeatureCatalog.TrialStarterCredits, balance.Single().Balance);
    }
}
