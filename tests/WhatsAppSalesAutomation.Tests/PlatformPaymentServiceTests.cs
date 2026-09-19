using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Payments screen is one list: each subscribed tenant's upcoming monthly charge first - visible before any payment
/// exists - then the payments themselves, paged together.</summary>
public sealed class PlatformPaymentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly PlatformPaymentService _payments;
    private readonly Plan _plan = new() { Code = "silver", Name = "Silver" };
    private readonly Tenant _mumbai = new() { Name = "Mumbai Co", Slug = "mumbai", CountryCode = "IN", StateCode = "MH", Status = TenantStatus.Active };
    private readonly Tenant _delhi = new() { Name = "Delhi Co", Slug = "delhi", CountryCode = "IN", StateCode = "DL", Status = TenantStatus.Active };
    private readonly Tenant _britain = new() { Name = "British Co", Slug = "british", CountryCode = "GB", Status = TenantStatus.Active };
    private readonly Tenant _germany = new() { Name = "German Co", Slug = "german", CountryCode = "DE", Status = TenantStatus.Active };
    private readonly Tenant _manual = new() { Name = "Manual Co", Slug = "manual", CountryCode = "IN", Status = TenantStatus.Active };
    private readonly Tenant _trial = new() { Name = "Trial Co", Slug = "trial", CountryCode = "IN", Status = TenantStatus.Trial };
    private static readonly DateTime NextCharge = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

    public PlatformPaymentServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _payments = new PlatformPaymentService(_db, TestPricing.Default());

        _db.Tenants.AddRange(_mumbai, _delhi, _britain, _germany, _manual, _trial);
        _db.Plans.Add(_plan);
        _db.PlanPrices.Add(new PlanPrice { PlanId = _plan.Id, CountryCode = "IN", Amount = 2500m });
        _db.PlanPrices.Add(new PlanPrice { PlanId = _plan.Id, CountryCode = "GB", Amount = 40m });
        foreach (var t in new[] { _mumbai, _delhi, _britain, _germany })
            _db.Subscriptions.Add(new Subscription { TenantId = t.Id, PlanId = _plan.Id, Status = SubscriptionStatus.Active, CurrentPeriodEndUtc = NextCharge });
        // Set by an operator: a plan, but no billing period yet.
        _db.Subscriptions.Add(new Subscription { TenantId = _manual.Id, PlanId = _plan.Id, Status = SubscriptionStatus.Active });
        // On trial with no plan: not a subscription.
        _db.Subscriptions.Add(new Subscription { TenantId = _trial.Id, Status = SubscriptionStatus.Trialing });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<Application.Common.Models.PagedResult<PlatformPaymentListItemDto>> Grid(PlatformPaymentQuery? query = null) =>
        _payments.GetPagedAsync(query ?? new PlatformPaymentQuery { PageSize = 50 });

    private Payment PaidSubscription(Tenant tenant, DateTime paidAt, decimal amount = 2500m) => new()
    {
        TenantId = tenant.Id, Kind = PaymentKind.Subscription, PlanName = "Silver", CurrencyCode = "INR", CurrencySymbol = "₹",
        LocalAmount = amount, PaidAtUtc = paidAt
    };

    [Fact]
    public async Task Every_subscribed_tenant_shows_as_an_upcoming_charge_before_any_payment_exists()
    {
        Assert.Empty(await _db.Payments.IgnoreQueryFilters().ToListAsync());

        var grid = await Grid();

        Assert.Equal(5, grid.TotalCount);
        Assert.DoesNotContain(grid.Items, g => g.TenantId == _trial.Id);
        Assert.Equal(3, grid.Items.Count(g => g.Status == PaymentRowStatus.Upcoming));
        var mumbai = grid.Items.Single(g => g.TenantId == _mumbai.Id);
        Assert.Equal(NextCharge, mumbai.DueAtUtc);
        Assert.Null(mumbai.PaidAtUtc);
        Assert.Equal("Subscription", mumbai.Kind);
        Assert.Equal("Silver", mumbai.Description);
    }

    [Fact]
    public async Task An_upcoming_charge_is_the_price_for_the_tenants_country_with_its_tax_on_top()
    {
        var items = (await Grid()).Items;

        var mumbai = items.Single(g => g.TenantId == _mumbai.Id);   // the platform's own state: CGST + SGST
        Assert.Equal("INR", mumbai.CurrencyCode);
        Assert.Equal(2500m, mumbai.LocalAmount);
        Assert.Equal(450m, mumbai.TaxLocal);
        Assert.Equal(2950m, mumbai.TotalLocal);
        Assert.Equal(new[] { "CGST", "SGST" }, mumbai.TaxLines!.Select(l => l.Name).ToArray());
        Assert.Equal(2950m, mumbai.AmountInr);

        Assert.Equal("IGST", Assert.Single(items.Single(g => g.TenantId == _delhi.Id).TaxLines!).Name);

        var britain = items.Single(g => g.TenantId == _britain.Id); // no tax rule for the UK
        Assert.Equal("GBP", britain.CurrencyCode);
        Assert.Equal(40m, britain.TotalLocal);
        Assert.Empty(britain.TaxLines!);
        Assert.Equal(4202.53m, britain.AmountInr);
    }

    [Fact]
    public async Task No_price_in_the_tenants_country_and_no_billing_period_are_told_apart_and_sorted_last()
    {
        var items = (await Grid()).Items;

        var germany = items.Single(g => g.TenantId == _germany.Id);
        Assert.Equal(PaymentRowStatus.NoPrice, germany.Status);
        Assert.Equal(0m, germany.TotalLocal);   // nothing is invented from a conversion
        Assert.Equal(0m, germany.AmountInr);

        var manual = items.Single(g => g.TenantId == _manual.Id);
        Assert.Equal(PaymentRowStatus.NotScheduled, manual.Status);
        Assert.Null(manual.DueAtUtc);
        Assert.Equal(2950m, manual.TotalLocal);   // still shows what it will be charged

        // Scheduled charges first, the rest after.
        Assert.All(items.Take(3), g => Assert.Equal(PaymentRowStatus.Upcoming, g.Status));
    }

    [Fact]
    public async Task Payments_follow_the_upcoming_charges_newest_first_and_are_marked_paid()
    {
        var older = new DateTime(2026, 8, 19, 8, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc);
        _db.Payments.AddRange(PaidSubscription(_mumbai, older), PaidSubscription(_mumbai, newer));
        await _db.SaveChangesAsync();

        var items = (await Grid()).Items;

        Assert.Equal(7, items.Count);
        Assert.All(items.Take(5), g => Assert.NotEqual(PaymentRowStatus.Paid, g.Status));
        Assert.Equal(new DateTime?[] { newer, older }, items.Skip(5).Select(g => g.PaidAtUtc).ToArray());
        Assert.All(items.Skip(5), g => Assert.Equal(PaymentRowStatus.Paid, g.Status));
    }

    [Fact]
    public async Task Paging_runs_across_both_as_one_list()
    {
        var paid = Enumerable.Range(1, 4).Select(i => PaidSubscription(_mumbai, new DateTime(2026, 9, i, 8, 0, 0, DateTimeKind.Utc))).ToArray();
        _db.Payments.AddRange(paid);
        await _db.SaveChangesAsync();   // 5 upcoming + 4 paid = 9 rows

        var first = await Grid(new PlatformPaymentQuery { Page = 1, PageSize = 4 });
        var second = await Grid(new PlatformPaymentQuery { Page = 2, PageSize = 4 });
        var third = await Grid(new PlatformPaymentQuery { Page = 3, PageSize = 4 });

        Assert.Equal(9, first.TotalCount);
        Assert.Equal(4, first.Items.Count);
        Assert.All(first.Items, g => Assert.NotEqual(PaymentRowStatus.Paid, g.Status));
        // Page two straddles the join: the last upcoming row, then the three newest payments.
        Assert.Equal(new[] { false, true, true, true }, second.Items.Select(g => g.Status == PaymentRowStatus.Paid).ToArray());
        Assert.Equal(new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc), second.Items[1].PaidAtUtc);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), Assert.Single(third.Items).PaidAtUtc);

        var everyId = first.Items.Concat(second.Items).Concat(third.Items).Select(g => g.Id).ToList();
        Assert.Equal(9, everyId.Distinct().Count());   // nothing repeated or dropped across pages
    }

    [Fact]
    public async Task Only_subscription_charges_are_upcoming_so_other_types_show_payments_alone()
    {
        _db.Payments.Add(new Payment { TenantId = _mumbai.Id, Kind = PaymentKind.CreditPack, PlanName = "Pack", CurrencyCode = "INR", CurrencySymbol = "₹", LocalAmount = 830m, PaidAtUtc = NextCharge });
        _db.Payments.Add(PaidSubscription(_mumbai, NextCharge.AddDays(-30)));
        await _db.SaveChangesAsync();

        var packs = await Grid(new PlatformPaymentQuery { Kind = PaymentKind.CreditPack, PageSize = 50 });
        Assert.Equal("Pack", Assert.Single(packs.Items).Description);
        Assert.Equal(1, packs.TotalCount);

        var subscriptions = await Grid(new PlatformPaymentQuery { Kind = PaymentKind.Subscription, PageSize = 50 });
        Assert.Equal(6, subscriptions.TotalCount);   // 5 upcoming + the paid one
    }

    [Fact]
    public async Task Search_and_the_tenant_filter_apply_to_the_upcoming_charges_too()
    {
        Assert.Equal("Mumbai Co", Assert.Single((await Grid(new PlatformPaymentQuery { Search = "Mumbai", PageSize = 50 })).Items).TenantName);
        Assert.Equal(5, (await Grid(new PlatformPaymentQuery { Search = "Silver", PageSize = 50 })).TotalCount);
        Assert.Empty((await Grid(new PlatformPaymentQuery { Search = "nothing-like-this", PageSize = 50 })).Items);
        Assert.Equal(_delhi.Id, Assert.Single((await Grid(new PlatformPaymentQuery { TenantId = _delhi.Id, PageSize = 50 })).Items).TenantId);
    }
}
