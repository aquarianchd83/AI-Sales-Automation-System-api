using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A tenant is told before its plan's period ends - a week out and the day before - so a renewal charge, or a plan that
/// cannot renew, is never a surprise.</summary>
public sealed class PlanExpiryNoticeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly RecordingNotifier _notifier = new();
    private readonly PlanExpiryNoticeService _notices;
    private readonly Plan _plan = new() { Code = "silver", Name = "Silver" };
    private readonly Tenant _mumbai = new() { Name = "Mumbai Co", Slug = "mumbai", CountryCode = "IN", StateCode = "MH", Status = TenantStatus.Active };
    private readonly Tenant _germany = new() { Name = "German Co", Slug = "german", CountryCode = "DE", Status = TenantStatus.Active };

    public PlanExpiryNoticeServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _notices = new PlanExpiryNoticeService(_db, _notifier, TestPricing.Default(), _clock);

        _db.Tenants.AddRange(_mumbai, _germany);
        _db.Plans.Add(_plan);
        _db.PlanPrices.Add(new PlanPrice { PlanId = _plan.Id, CountryCode = "IN", Amount = 2500m });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Subscription Subscribe(Tenant tenant, DateTime endsAtUtc, SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var subscription = new Subscription { TenantId = tenant.Id, PlanId = _plan.Id, Status = status, CurrentPeriodEndUtc = endsAtUtc };
        _db.Subscriptions.Add(subscription);
        _db.SaveChanges();
        return subscription;
    }

    [Fact]
    public async Task A_week_before_the_period_ends_the_tenant_is_told_what_will_be_charged_and_when()
    {
        Subscribe(_mumbai, _clock.UtcNow.AddDays(6.5));

        Assert.Equal(1, await _notices.RunAsync());

        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal(TenantNotificationKind.PlanExpiring7, notice.Kind);
        Assert.Equal(_mumbai.Id, notice.TenantId);
        Assert.Equal("Your Silver plan renews in 7 days", notice.Title);
        // 2,500 plus 18% GST = 2,950, on the date the period ends.
        Assert.Contains("renews on 17 Sep 2026", notice.Body);
        Assert.Contains("₹2950 (₹2500 plus tax)", notice.Body);
        Assert.True(notice.AlsoWhatsApp);
    }

    [Fact]
    public async Task The_day_before_it_is_a_final_reminder_and_the_two_are_different_notices()
    {
        Subscribe(_mumbai, _clock.UtcNow.AddDays(5));
        await _notices.RunAsync();

        _clock.UtcNow = _clock.UtcNow.AddDays(4.5);   // now half a day from the end
        await _notices.RunAsync();

        Assert.Equal(new[] { TenantNotificationKind.PlanExpiring7, TenantNotificationKind.PlanExpiring1 }, _notifier.Sent.Select(n => n.Kind).ToArray());
        Assert.Equal("Your Silver plan renews tomorrow", _notifier.Sent[1].Title);
    }

    [Fact]
    public async Task Running_again_never_repeats_a_notice_for_the_same_period()
    {
        Subscribe(_mumbai, _clock.UtcNow.AddDays(3));

        await _notices.RunAsync();
        await _notices.RunAsync();
        _clock.UtcNow = _clock.UtcNow.AddHours(6);
        await _notices.RunAsync();

        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public async Task Outside_the_window_or_not_running_means_no_notice()
    {
        var far = Subscribe(_mumbai, _clock.UtcNow.AddDays(20));
        Assert.Equal(0, await _notices.RunAsync());

        // Already past its end: the renewal job's business, not a warning.
        far.CurrentPeriodEndUtc = _clock.UtcNow.AddHours(-2);
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _notices.RunAsync());

        // Inside the window but not active (past due, cancelled): no "renews" promise to make.
        far.CurrentPeriodEndUtc = _clock.UtcNow.AddDays(3);
        far.Status = SubscriptionStatus.PastDue;
        await _db.SaveChangesAsync();
        Assert.Equal(0, await _notices.RunAsync());

        Assert.Empty(_notifier.Sent);
    }

    [Fact]
    public async Task A_plan_with_no_price_in_the_tenants_country_says_it_cannot_renew_rather_than_promising_a_charge()
    {
        Subscribe(_germany, _clock.UtcNow.AddDays(2));

        await _notices.RunAsync();

        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal("Your Silver plan ends in 2 days", notice.Title);
        Assert.Contains("can't renew automatically", notice.Body);
        Assert.DoesNotContain("charged", notice.Body);
    }

    [Fact]
    public async Task The_next_period_gets_its_own_notices_and_a_tenant_with_no_plan_gets_none()
    {
        var subscription = Subscribe(_mumbai, _clock.UtcNow.AddDays(3));
        _db.Subscriptions.Add(new Subscription { TenantId = _germany.Id, Status = SubscriptionStatus.Active, CurrentPeriodEndUtc = _clock.UtcNow.AddDays(3) });
        await _db.SaveChangesAsync();
        await _notices.RunAsync();
        Assert.Single(_notifier.Sent);

        // Renewed a month on: the same tenant, a new period, a new notice.
        subscription.CurrentPeriodEndUtc = _clock.UtcNow.AddDays(3).AddMonths(1);
        await _db.SaveChangesAsync();
        _clock.UtcNow = _clock.UtcNow.AddMonths(1);
        await _notices.RunAsync();

        Assert.Equal(2, _notifier.Sent.Count);
        Assert.All(_notifier.Sent, n => Assert.Equal(_mumbai.Id, n.TenantId));
        Assert.NotEqual(_notifier.Sent[0].EpisodeKey, _notifier.Sent[1].EpisodeKey);
    }
}
