using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Multi-country pricing: a plan or pack costs what is set for the tenant's own country, with that country's tax
/// added on top, and is simply not sold where no price is set. Nothing is converted from a USD price.</summary>
public sealed class CountryPricingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly PlatformBillingService _catalog;
    private readonly BillingService _billing;
    private readonly Tenant _mumbai = new() { Name = "Mumbai Co", Slug = "mumbai", CountryCode = "IN", StateCode = "MH", Status = TenantStatus.Active };
    private readonly Tenant _delhi = new() { Name = "Delhi Co", Slug = "delhi", CountryCode = "IN", StateCode = "DL", Status = TenantStatus.Active };
    private readonly Tenant _britain = new() { Name = "British Co", Slug = "british", CountryCode = "GB", Status = TenantStatus.Active };
    private readonly Tenant _elsewhere = new() { Name = "German Co", Slug = "german", CountryCode = "DE", Status = TenantStatus.Active };

    public CountryPricingTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        var ledger = new QuotaLedgerService(_db, _clock);
        var operatorPricing = Fake.Of<ICurrentUserPricingService>((_, _) => Task.FromResult(RegionalPricingCatalog.Resolve("IN")));
        _catalog = new PlatformBillingService(
            _db, operatorPricing,
            new CreatePlanRequestValidator(_db), new UpdatePlanRequestValidator(),
            new CreateCreditPackRequestValidator(), new UpdateCreditPackRequestValidator());

        var jobs = Fake.Of<ITenantJobProvisioner>((m, _) => m.Name == nameof(ITenantJobProvisioner.SyncTenantAsync) ? Task.CompletedTask : throw new NotImplementedException(m.Name));
        // India's GST at 18%, the platform registered in Maharashtra.
        _billing = new BillingService(_db, new PlatformContext(), _clock, jobs, ledger, TestPricing.Default());

        _db.Tenants.AddRange(_mumbai, _delhi, _britain, _elsewhere);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CreatePlanRequest Plan(string code, params CountryPriceInput[] prices) =>
        new(code, "Pro", 5, 1000, 5, 20, 0, 50, null, prices);

    private Task<PlatformPlanDto> ProPlan() =>
        _catalog.CreatePlanAsync(Plan("pro", new CountryPriceInput("IN", 999m), new CountryPriceInput("GB", 40m)));

    [Fact]
    public async Task A_plan_is_quoted_only_in_the_countries_it_has_a_price_for()
    {
        await ProPlan();

        Assert.Equal(999m, (await _billing.GetPlansAsync("IN")).Single().LocalPriceAmount);
        Assert.Equal(40m, (await _billing.GetPlansAsync("GB")).Single().LocalPriceAmount);
        // Germany has no price, so the plan is not offered there - never shown at a converted price.
        Assert.Empty(await _billing.GetPlansAsync("DE"));
    }

    [Fact]
    public async Task A_plan_with_no_price_in_the_tenants_country_cannot_be_bought_there()
    {
        var plan = await ProPlan();

        await Assert.ThrowsAsync<ConflictException>(() => _billing.ChoosePlanAsync(_elsewhere.Id, plan.Id));
        Assert.Empty(await _db.Payments.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Gst_is_added_on_top_and_splits_into_cgst_and_sgst_inside_the_platforms_state()
    {
        var plan = await ProPlan();

        await _billing.ChoosePlanAsync(_mumbai.Id, plan.Id);   // same state as the platform (MH)
        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync();

        Assert.Equal(999m, payment.LocalAmount);          // the price
        Assert.Equal(179.82m, payment.TaxLocal);          // 18% on top
        Assert.Equal(1178.82m, payment.TotalLocal);       // what was paid
        Assert.Equal("INR", payment.CurrencyCode);
        Assert.Equal("IN", payment.CountryCode);
        Assert.Equal("MH", payment.StateCode);

        var dto = PaymentDto.From(payment);
        Assert.Equal(new[] { "CGST", "SGST" }, dto.TaxLines!.Select(l => l.Name).ToArray());
        Assert.All(dto.TaxLines!, l => Assert.Equal(9m, l.RatePercent));
        Assert.All(dto.TaxLines!, l => Assert.Equal(89.91m, l.Amount));
        Assert.Equal(1178.82m, dto.TotalLocal);
    }

    [Fact]
    public async Task Gst_is_one_igst_line_in_any_other_state_or_when_the_state_is_unknown()
    {
        var plan = await ProPlan();
        var nowhere = new Tenant { Name = "No State", Slug = "nostate", CountryCode = "IN", Status = TenantStatus.Active };
        _db.Tenants.Add(nowhere);
        await _db.SaveChangesAsync();

        await _billing.ChoosePlanAsync(_delhi.Id, plan.Id);
        await _billing.ChoosePlanAsync(nowhere.Id, plan.Id);

        foreach (var tenantId in new[] { _delhi.Id, nowhere.Id })
        {
            var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync(p => p.TenantId == tenantId);
            var lines = PaymentDto.From(payment).TaxLines!;
            Assert.Equal("IGST", Assert.Single(lines).Name);
            Assert.Equal(18m, lines[0].RatePercent);
            Assert.Equal(179.82m, payment.TaxLocal);
        }
    }

    [Fact]
    public async Task A_country_with_no_tax_rule_pays_no_tax_and_the_payment_carries_the_rupee_equivalent()
    {
        var plan = await ProPlan();

        await _billing.ChoosePlanAsync(_britain.Id, plan.Id);
        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync();

        Assert.Equal("GBP", payment.CurrencyCode);
        Assert.Equal(40m, payment.LocalAmount);
        Assert.Equal(0m, payment.TaxLocal);
        Assert.Equal(40m, payment.TotalPaidLocal);
        // 83 rupees a dollar, 0.79 pounds a dollar: 40 GBP = 40 / 0.79 x 83 = about Rs 4,202.53, frozen at the rate of the day.
        Assert.Equal(105.063291m, payment.FxRateToInr);
        Assert.Equal(4202.53m, payment.AmountInr);
    }

    [Fact]
    public async Task The_quote_shown_to_a_tenant_matches_what_it_is_charged()
    {
        var plan = await ProPlan();
        await _db.Database.ExecuteSqlRawAsync("UPDATE Tenants SET StateCode = 'DL' WHERE Slug = 'delhi'");

        var plans = await _billing.GetPlansAsync("IN");
        var shown = plans.Single();
        Assert.Equal(999m, shown.LocalPriceAmount);
        // Anonymous preview: state unknown, so IGST.
        Assert.Equal(179.82m, shown.TaxLocal);
        Assert.Equal(1178.82m, shown.TotalLocal);
        Assert.Equal("IGST", shown.TaxLines.Single().Name);

        await _billing.ChoosePlanAsync(_delhi.Id, plan.Id);
        Assert.Equal(1178.82m, (await _db.Payments.IgnoreQueryFilters().SingleAsync()).TotalLocal);
    }

    [Fact]
    public async Task Credit_packs_follow_the_same_rules_priced_per_country_with_tax_and_hidden_where_unpriced()
    {
        var pack = await _catalog.CreateCreditPackAsync(new CreateCreditPackRequest(
            QuotaType.AiConversations, "1,000 AI conversations", 1000, 0, new[] { new CountryPriceInput("IN", 999m) }));
        var plan = await ProPlan();
        await _billing.ChoosePlanAsync(_mumbai.Id, plan.Id);
        await _billing.ChoosePlanAsync(_britain.Id, plan.Id);

        var offered = (await _billing.GetCreditPacksAsync(_mumbai.Id)).Single();
        Assert.Equal(999m, offered.LocalPriceAmount);
        Assert.Equal(1178.82m, offered.TotalLocal);
        Assert.Empty(await _billing.GetCreditPacksAsync(_britain.Id));   // no British price: not sold there

        var payment = await _billing.PurchaseCreditPackAsync(_mumbai.Id, pack.Id);
        Assert.Equal(999m, payment.LocalAmount);
        Assert.Equal(1178.82m, payment.TotalLocal);
        await Assert.ThrowsAsync<ConflictException>(() => _billing.PurchaseCreditPackAsync(_britain.Id, pack.Id));
    }

    [Fact]
    public async Task Country_prices_on_update_are_the_complete_set_and_null_leaves_them_alone()
    {
        var plan = await ProPlan();
        Assert.Equal(2, plan.CountryPrices.Count);

        var request = new UpdatePlanRequest("Pro", 5, 1000, 5, 20, 0, true);

        Assert.Equal(2, (await _catalog.UpdatePlanAsync(plan.Id, request)).CountryPrices.Count);

        var updated = await _catalog.UpdatePlanAsync(plan.Id, request with
        {
            CountryPrices = new[] { new CountryPriceInput("IN", 1200m), new CountryPriceInput("DE", 55m) }
        });

        Assert.Equal(new[] { "DE", "IN" }, updated.CountryPrices.Select(p => p.CountryCode).OrderBy(c => c).ToArray());
        // The operator is in India: their own list shows the Indian price.
        Assert.Equal(1200m, updated.PriceMonthlyLocal);
    }

    [Fact]
    public async Task Taking_a_price_away_stops_the_plan_being_sold_there()
    {
        var plan = await ProPlan();

        await _catalog.UpdatePlanAsync(plan.Id, new UpdatePlanRequest("Pro", 5, 1000, 5, 20, 0, true,
            CountryPrices: new[] { new CountryPriceInput("IN", 0m), new CountryPriceInput("GB", 40m) }));

        Assert.Empty(await _billing.GetPlansAsync("IN"));
        Assert.Single(await _billing.GetPlansAsync("GB"));
    }

    [Fact]
    public async Task A_country_the_platform_does_not_price_for_is_rejected_and_so_is_a_duplicate()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreatePlanAsync(Plan("a", new CountryPriceInput("ZZ", 10m))));
        await Assert.ThrowsAsync<ValidationException>(() =>
            _catalog.CreatePlanAsync(Plan("b", new CountryPriceInput("IN", 10m), new CountryPriceInput("in", 20m))));
        await Assert.ThrowsAsync<ValidationException>(() => _catalog.CreatePlanAsync(Plan("c", new CountryPriceInput("IN", -1m))));
    }

    [Fact]
    public void Tax_rules_come_from_configuration_and_a_rate_of_zero_means_no_tax()
    {
        var vat = new PricingService(
            new FixedOptions<WhatsAppSalesAutomation.Application.Common.Options.TaxOptions>(new()
            {
                Countries = new(StringComparer.OrdinalIgnoreCase) { ["DE"] = new() { Name = "VAT", RatePercent = 19m }, ["IN"] = new() { Name = "GST", RatePercent = 0m, SplitByState = true } }
            }),
            new FixedOptions<WhatsAppSalesAutomation.Application.Common.Options.FxOptions>(new() { InrPerUsd = 90m }));

        var de = vat.Quote(100m, "DE", null)!;
        Assert.Equal("VAT", Assert.Single(de.TaxLines).Name);
        Assert.Equal(19m, de.Tax);
        Assert.Equal(119m, de.Total);
        Assert.Equal(0m, vat.Quote(100m, "IN", "MH")!.Tax);
        // Nothing is priced without a price.
        Assert.Null(vat.Quote(null, "DE", null));
        Assert.Null(vat.Quote(0m, "DE", null));
        // The rupee rate is the one set by hand.
        Assert.Equal(Math.Round(90m / 0.92m, 6), de.FxRateToInr);
    }
}
