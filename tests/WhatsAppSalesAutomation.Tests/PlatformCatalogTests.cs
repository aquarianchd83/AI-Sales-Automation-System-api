using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The operator's catalog: what each plan includes and which credit packs tenants can buy. The point of
/// these is that a plan or pack the operator creates is actually usable - the gap that made a console-created
/// plan hand its tenants nothing.</summary>
public sealed class PlatformCatalogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly PlatformBillingService _catalog;
    private readonly BillingService _tenantBilling;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme", CountryCode = "IN", Status = TenantStatus.Active };

    public PlatformCatalogTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        var pricing = Fake.Of<ICurrentUserPricingService>((_, _) => Task.FromResult(new RegionalPricing("IN", "India", "INR", "₹", 83m)));
        _catalog = new PlatformBillingService(
            _db, pricing,
            new CreatePlanRequestValidator(_db), new UpdatePlanRequestValidator(),
            new CreateCreditPackRequestValidator(), new UpdateCreditPackRequestValidator());

        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        _tenantBilling = new BillingService(_db, new PlatformContext(), _clock, jobs, _ledger, TestPricing.NoTax());

        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CreatePlanRequest NewPlan(string code, params PlanQuotaInput[] quotas) =>
        new(code, "Pro", 5, 1000, 5, 20, 5900, 50, quotas, new[] { new CountryPriceInput("IN", 4897m) });

    // Prices are per country: these go in at the price for India, where the test tenant is (USD cents x 83).
    private static CreateCreditPackRequest Pack(QuotaType type, string name, decimal units, int cents) =>
        new(type, name, units, cents, new[] { new CountryPriceInput("IN", Math.Round(cents / 100m * 83m, 2)) });

    private static UpdateCreditPackRequest Repack(string name, decimal units, int cents, bool active) =>
        new(name, units, cents, active, new[] { new CountryPriceInput("IN", Math.Round(cents / 100m * 83m, 2)) });

    private async Task<decimal> BalanceAsync(QuotaType type) =>
        (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == type).Balance;

    // ---- Plans and their included quotas

    [Fact]
    public async Task A_new_plan_carries_its_quotas_and_a_tenant_choosing_it_actually_receives_them()
    {
        var plan = await _catalog.CreatePlanAsync(NewPlan("pro",
            new PlanQuotaInput(QuotaType.WhatsAppMessages, 2000),
            new PlanQuotaInput(QuotaType.AiConversations, 1500),
            new PlanQuotaInput(QuotaType.LeadCandidates, 0))); // 0 means "none" - no row

        Assert.Equal(new[] { QuotaType.WhatsAppMessages, QuotaType.AiConversations }, plan.IncludedQuotas.Select(q => q.QuotaType).ToArray());
        Assert.Equal(2, await _db.PlanQuotas.CountAsync(q => q.PlanId == plan.Id));

        await _tenantBilling.ChoosePlanAsync(_tenant.Id, plan.Id);

        Assert.Equal(2000m, await BalanceAsync(QuotaType.WhatsAppMessages));
        Assert.Equal(1500m, await BalanceAsync(QuotaType.AiConversations));
        Assert.Equal(0m, await BalanceAsync(QuotaType.LeadCandidates));
    }

    [Fact]
    public async Task Listing_plans_shows_each_plans_quotas()
    {
        await _catalog.CreatePlanAsync(NewPlan("pro", new PlanQuotaInput(QuotaType.LeadCandidates, 250)));
        await _catalog.CreatePlanAsync(NewPlan("bare"));

        var plans = await _catalog.GetPlansAsync();

        Assert.Equal(250m, plans.Single(p => p.Code == "pro").IncludedQuotas.Single().Units);
        Assert.Empty(plans.Single(p => p.Code == "bare").IncludedQuotas);
    }

    [Fact]
    public async Task Updating_with_a_list_replaces_the_set_and_without_one_leaves_it_alone()
    {
        var plan = await _catalog.CreatePlanAsync(NewPlan("pro",
            new PlanQuotaInput(QuotaType.WhatsAppMessages, 2000), new PlanQuotaInput(QuotaType.AiConversations, 1500)));

        UpdatePlanRequest Update(IReadOnlyList<PlanQuotaInput>? quotas) => new("Pro", 5, 1000, 5, 20, 5900, true, 50, quotas);

        var untouched = await _catalog.UpdatePlanAsync(plan.Id, Update(null));
        Assert.Equal(2, untouched.IncludedQuotas.Count);

        var changed = await _catalog.UpdatePlanAsync(plan.Id, Update(new[]
        {
            new PlanQuotaInput(QuotaType.WhatsAppMessages, 3000), // raised
            new PlanQuotaInput(QuotaType.AiConversations, 0),     // dropped
            new PlanQuotaInput(QuotaType.LeadCandidates, 100),    // added
        }));

        Assert.Equal(new[] { (QuotaType.WhatsAppMessages, 3000m), (QuotaType.LeadCandidates, 100m) },
            changed.IncludedQuotas.Select(q => (q.QuotaType, q.Units)).ToArray());
        Assert.Equal(2, await _db.PlanQuotas.CountAsync(q => q.PlanId == plan.Id));
    }

    [Fact]
    public async Task Changing_a_plans_quota_never_rewrites_what_a_tenant_already_received()
    {
        var plan = await _catalog.CreatePlanAsync(NewPlan("pro", new PlanQuotaInput(QuotaType.WhatsAppMessages, 2000)));
        await _tenantBilling.ChoosePlanAsync(_tenant.Id, plan.Id);

        await _catalog.UpdatePlanAsync(plan.Id, new UpdatePlanRequest("Pro", 5, 1000, 5, 20, 5900, true, 50,
            new[] { new PlanQuotaInput(QuotaType.WhatsAppMessages, 100) }));

        Assert.Equal(2000m, await BalanceAsync(QuotaType.WhatsAppMessages)); // this period is as granted
    }

    [Fact]
    public async Task Bad_quota_lists_are_refused()
    {
        var duplicate = NewPlan("dup", new PlanQuotaInput(QuotaType.WhatsAppMessages, 10), new PlanQuotaInput(QuotaType.WhatsAppMessages, 20));
        var negative = NewPlan("neg", new PlanQuotaInput(QuotaType.WhatsAppMessages, -1));
        var huge = NewPlan("huge", new PlanQuotaInput(QuotaType.WhatsAppMessages, 2_000_000_000m));

        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreatePlanAsync(duplicate));
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreatePlanAsync(negative));
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreatePlanAsync(huge));
        Assert.Empty(await _db.Plans.ToListAsync());
    }

    // ---- Credit packs

    [Fact]
    public async Task A_pack_the_operator_creates_is_priced_in_their_currency_and_offered_to_tenants()
    {
        var pack = await _catalog.CreateCreditPackAsync(Pack(QuotaType.AiConversations, " 2,000 AI conversations ", 2000, 1500));

        Assert.Equal("2,000 AI conversations", pack.Name);
        Assert.Equal("INR", pack.CurrencyCode);
        Assert.Equal(1245m, pack.PriceLocal); // $15 at 83
        Assert.True(pack.IsActive);

        var offered = await _tenantBilling.GetCreditPacksAsync(_tenant.Id);
        Assert.Contains(offered, p => p.Id == pack.Id && p.LocalPriceAmount == 1245m);
    }

    [Fact]
    public async Task Editing_a_pack_changes_it_for_future_buyers_but_not_for_a_purchase_already_made()
    {
        await _tenantBilling.ChoosePlanAsync(_tenant.Id, (await _catalog.CreatePlanAsync(NewPlan("pro"))).Id);
        var pack = await _catalog.CreateCreditPackAsync(Pack(QuotaType.AiConversations, "1,000 AI", 1000, 800));
        var bought = await _tenantBilling.PurchaseCreditPackAsync(_tenant.Id, pack.Id);

        await _catalog.UpdateCreditPackAsync(pack.Id, Repack("500 AI", 500, 1200, true));

        Assert.Equal(1000m, await BalanceAsync(QuotaType.AiConversations)); // the credits it bought stay 1,000
        Assert.Equal(800, (await _db.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == bought.Id)).AmountCents); // at the price it paid

        var next = await _tenantBilling.PurchaseCreditPackAsync(_tenant.Id, pack.Id);
        Assert.Equal(1200, next.AmountCents);
        Assert.Equal(1500m, await BalanceAsync(QuotaType.AiConversations));
    }

    [Fact]
    public async Task A_retired_pack_disappears_from_the_tenant_catalog_cannot_be_bought_and_can_come_back()
    {
        await _tenantBilling.ChoosePlanAsync(_tenant.Id, (await _catalog.CreatePlanAsync(NewPlan("pro"))).Id);
        var pack = await _catalog.CreateCreditPackAsync(Pack(QuotaType.LeadCandidates, "100 leads", 100, 600));

        await _catalog.DeactivateCreditPackAsync(pack.Id);
        await _catalog.DeactivateCreditPackAsync(pack.Id); // already retired - a no-op, not an error

        Assert.DoesNotContain(await _tenantBilling.GetCreditPacksAsync(_tenant.Id), p => p.Id == pack.Id);
        Assert.Contains(await _catalog.GetCreditPacksAsync(), p => p.Id == pack.Id && !p.IsActive); // the operator still sees it
        await Assert.ThrowsAsync<NotFoundException>(() => _tenantBilling.PurchaseCreditPackAsync(_tenant.Id, pack.Id));

        await _catalog.UpdateCreditPackAsync(pack.Id, Repack("100 leads", 100, 600, true));
        Assert.Contains(await _tenantBilling.GetCreditPacksAsync(_tenant.Id), p => p.Id == pack.Id);
    }

    [Fact]
    public async Task Nonsense_packs_are_refused_and_an_unknown_pack_is_not_found()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreateCreditPackAsync(new CreateCreditPackRequest(QuotaType.AiConversations, "", 100, 100)));
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreateCreditPackAsync(new CreateCreditPackRequest(QuotaType.AiConversations, "Empty", 0, 100)));
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreateCreditPackAsync(new CreateCreditPackRequest((QuotaType)99, "Bad type", 100, 100)));
        await Assert.ThrowsAsync<NotFoundException>(() => _catalog.UpdateCreditPackAsync(Guid.NewGuid(), new UpdateCreditPackRequest("x", 1, 1, true)));
        Assert.Empty(await _db.CreditPacks.ToListAsync());
    }
}
