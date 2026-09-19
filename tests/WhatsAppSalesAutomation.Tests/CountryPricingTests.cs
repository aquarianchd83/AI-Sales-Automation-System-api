using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A price set for a country is what a tenant in that country is quoted and charged - exactly, not a
/// conversion of the USD price. A country without one still gets the converted price.</summary>
public sealed class CountryPricingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly PlatformBillingService _catalog;
    private readonly BillingService _billing;
    private readonly Tenant _india = new() { Name = "Indian Co", Slug = "indian", CountryCode = "IN", Status = TenantStatus.Active };
    private readonly Tenant _britain = new() { Name = "British Co", Slug = "british", CountryCode = "GB", Status = TenantStatus.Active };

    public CountryPricingTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        var ledger = new QuotaLedgerService(_db, _clock);
        var operatorPricing = Fake.Of<ICurrentUserPricingService>((_, _) => Task.FromResult(new RegionalPricing("IN", "India", "INR", "₹", 83m)));
        _catalog = new PlatformBillingService(
            _db, operatorPricing,
            new CreatePlanRequestValidator(_db), new UpdatePlanRequestValidator(),
            new CreateCreditPackRequestValidator(), new UpdateCreditPackRequestValidator());

        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        _billing = new BillingService(_db, new PlatformContext(), _clock, jobs, ledger);

        _db.Tenants.AddRange(_india, _britain);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CreatePlanRequest Plan(string code, params CountryPriceInput[] prices) =>
        new(code, "Pro", 5, 1000, 5, 20, 5900, 50, null, prices);

    [Fact]
    public async Task A_tenant_is_quoted_and_charged_the_price_set_for_its_country()
    {
        var plan = await _catalog.CreatePlanAsync(Plan("pro", new CountryPriceInput("IN", 999m)));

        // India has an explicit price; Britain does not, so it gets the USD price converted (59 * 0.79).
        _india.CountryCode = "IN";
        var indian = (await _billing.GetPlansAsync("IN")).Single();
        var british = (await _billing.GetPlansAsync("GB")).Single();
        Assert.Equal(999m, indian.LocalPriceAmount);
        Assert.Equal(46.61m, british.LocalPriceAmount);

        await _billing.ChoosePlanAsync(_india.Id, plan.Id);
        await _billing.ChoosePlanAsync(_britain.Id, plan.Id);

        var payments = await _db.Payments.IgnoreQueryFilters().ToListAsync();
        var inPayment = payments.Single(p => p.TenantId == _india.Id);
        var gbPayment = payments.Single(p => p.TenantId == _britain.Id);

        Assert.Equal(999m, inPayment.LocalAmount);
        Assert.Equal("INR", inPayment.CurrencyCode);
        // The USD figure is derived from what was charged: 999 / 83 = 12.04 USD.
        Assert.Equal(1204, inPayment.AmountCents);
        Assert.Equal(46.61m, gbPayment.LocalAmount);
        Assert.Equal(5900, gbPayment.AmountCents);
    }

    [Fact]
    public async Task Country_prices_on_update_are_the_complete_set_and_null_leaves_them_alone()
    {
        var plan = await _catalog.CreatePlanAsync(Plan("pro", new CountryPriceInput("IN", 999m), new CountryPriceInput("GB", 40m)));
        Assert.Equal(2, plan.CountryPrices.Count);

        var request = new UpdatePlanRequest("Pro", 5, 1000, 5, 20, 5900, true);

        // Null: untouched.
        var untouched = await _catalog.UpdatePlanAsync(plan.Id, request);
        Assert.Equal(2, untouched.CountryPrices.Count);

        // A list: India is changed, Britain (absent) is removed, Germany added.
        var updated = await _catalog.UpdatePlanAsync(plan.Id, request with
        {
            CountryPrices = new[] { new CountryPriceInput("IN", 1200m), new CountryPriceInput("DE", 55m) }
        });

        Assert.Equal(new[] { "DE", "IN" }, updated.CountryPrices.Select(p => p.CountryCode).OrderBy(c => c).ToArray());
        Assert.Equal(1200m, updated.CountryPrices.Single(p => p.CountryCode == "IN").Amount);
        Assert.Equal("EUR", updated.CountryPrices.Single(p => p.CountryCode == "DE").CurrencyCode);
    }

    [Fact]
    public async Task A_price_of_zero_removes_that_countrys_price_so_it_falls_back_to_the_conversion()
    {
        var plan = await _catalog.CreatePlanAsync(Plan("pro", new CountryPriceInput("IN", 999m)));

        await _catalog.UpdatePlanAsync(plan.Id, new UpdatePlanRequest("Pro", 5, 1000, 5, 20, 5900, true,
            CountryPrices: new[] { new CountryPriceInput("IN", 0m) }));

        Assert.Equal(4897m, (await _billing.GetPlansAsync("IN")).Single().LocalPriceAmount);
    }

    [Fact]
    public async Task The_operators_own_catalog_shows_their_countrys_price()
    {
        await _catalog.CreatePlanAsync(Plan("pro", new CountryPriceInput("IN", 999m)));

        // The fake operator is in India, so their list shows the explicit Indian price, not 59 * 83.
        Assert.Equal(999m, (await _catalog.GetPlansAsync()).Single().PriceMonthlyLocal);
    }

    [Fact]
    public async Task Credit_packs_are_priced_and_charged_per_country_too()
    {
        var pack = await _catalog.CreateCreditPackAsync(new CreateCreditPackRequest(
            QuotaType.AiConversations, "1,000 AI conversations", 1000, 1400, new[] { new CountryPriceInput("IN", 999m) }));

        Assert.Equal(999m, (await _billing.GetCreditPacksAsync(_india.Id)).Single().LocalPriceAmount);
        Assert.Equal(11.06m, (await _billing.GetCreditPacksAsync(_britain.Id)).Single().LocalPriceAmount);

        var plan = await _catalog.CreatePlanAsync(Plan("pro"));
        await _billing.ChoosePlanAsync(_india.Id, plan.Id);
        var payment = await _billing.PurchaseCreditPackAsync(_india.Id, pack.Id);

        Assert.Equal(999m, payment.LocalAmount);
        Assert.Equal(1204, payment.AmountCents);
    }

    [Fact]
    public async Task A_country_the_platform_does_not_price_for_is_rejected_and_so_is_a_duplicate()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _catalog.CreatePlanAsync(Plan("a", new CountryPriceInput("ZZ", 10m))));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _catalog.CreatePlanAsync(Plan("b", new CountryPriceInput("IN", 10m), new CountryPriceInput("in", 20m))));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _catalog.CreatePlanAsync(Plan("c", new CountryPriceInput("IN", -1m))));
    }
}
