using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class SubscriptionRenewalServiceTests : IDisposable
{
    private static readonly DateTime PeriodStart = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Clock _clock = new() { UtcNow = PeriodStart.AddMonths(1).AddHours(2) };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly SubscriptionRenewalService _renewal;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme", CountryCode = "IN" };
    private readonly Plan _plan = new() { Code = "starter", Name = "Starter", PriceMonthlyCents = 3900 };

    public SubscriptionRenewalServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new NoTenant(), new NoUser());
        _db.Database.EnsureCreated();
        _ledger = new QuotaLedgerService(_db, _clock);
        _renewal = new SubscriptionRenewalService(_db, _ledger, _clock, TestPricing.NoTax());

        _db.Tenants.Add(_tenant);
        _db.Plans.Add(_plan);
        _db.PlanPrices.Add(new PlanPrice { PlanId = _plan.Id, CountryCode = "IN", Amount = 3237m });
        _db.PlanQuotas.Add(new PlanQuota { PlanId = _plan.Id, QuotaType = QuotaType.WhatsAppMessages, IncludedUnits = 1000 });
        _db.Subscriptions.Add(new Subscription
        {
            TenantId = _tenant.Id,
            PlanId = _plan.Id,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStartUtc = PeriodStart,
            CurrentPeriodEndUtc = PeriodStart.AddMonths(1)
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_due_subscription_is_charged_and_gets_a_fresh_allocation_once()
    {
        var (renewed, _) = await _renewal.RunAsync();
        var (again, _) = await _renewal.RunAsync();

        Assert.Equal(1, renewed);
        Assert.Equal(0, again);

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PeriodStart.AddMonths(1), subscription.CurrentPeriodStartUtc);
        Assert.Equal(PeriodStart.AddMonths(2), subscription.CurrentPeriodEndUtc);

        var payment = await _db.Payments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentKind.Subscription, payment.Kind);
        Assert.Equal(3900, payment.AmountCents);
        Assert.Equal("INR", payment.CurrencyCode);

        var balance = (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == QuotaType.WhatsAppMessages);
        Assert.Equal(1000m, balance.Balance);
    }

    [Fact]
    public async Task Months_nobody_was_served_are_skipped_not_billed()
    {
        _clock.UtcNow = PeriodStart.AddMonths(4).AddDays(3);

        await _renewal.RunAsync();

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PeriodStart.AddMonths(4), subscription.CurrentPeriodStartUtc);
        Assert.Equal(1, await _db.Payments.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Cancelled_and_not_yet_due_subscriptions_are_left_alone()
    {
        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        subscription.Status = SubscriptionStatus.Canceled;
        await _db.SaveChangesAsync();

        var (renewed, _) = await _renewal.RunAsync();
        Assert.Equal(0, renewed);

        subscription.Status = SubscriptionStatus.Active;
        _clock.UtcNow = PeriodStart.AddDays(10);
        await _db.SaveChangesAsync();
        (renewed, _) = await _renewal.RunAsync();
        Assert.Equal(0, renewed);
        Assert.Empty(await _db.Payments.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
