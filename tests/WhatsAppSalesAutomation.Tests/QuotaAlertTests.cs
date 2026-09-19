using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class QuotaAlertTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly FakeEmail _email = new();
    private readonly FakePlatformWhatsApp _whatsApp = new();
    private readonly QuotaAlertService _alerts;
    private readonly Tenant _tenant = new()
    {
        Name = "Acme", Slug = "acme", Status = TenantStatus.Active,
        BillingAlertEmail = "billing@acme.test", BillingAlertPhoneE164 = "+919876543210", BillingAlertWhatsAppEnabled = true
    };

    public QuotaAlertTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        // UserManager is only reached when a tenant has neither an alert email nor an owner - not exercised here.
        var notifier = new TenantNotifier(_db, null!, _email, _whatsApp, Options.Create(new BillingAlertOptions()), NullLogger<TenantNotifier>.Instance);
        _alerts = new QuotaAlertService(_db, notifier, _clock);

        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task GrantAsync(decimal units) =>
        _ledger.AdjustAsync(_tenant.Id, QuotaType.WhatsAppMessages, units, "test funding", Guid.NewGuid());

    private Task SpendAsync(decimal units, string key) =>
        _ledger.ConsumeAsync(new ConsumeRequest(_tenant.Id, QuotaType.WhatsAppMessages, units, key, "Message", key));

    private Task<List<TenantNotification>> NotificationsAsync() =>
        _db.TenantNotifications.IgnoreQueryFilters().OrderBy(n => n.CreatedAt).ToListAsync();

    [Fact]
    public async Task Plenty_of_quota_raises_nothing()
    {
        await GrantAsync(1000);
        await SpendAsync(500, "m1");

        Assert.Equal(0, await _alerts.EvaluateAsync());
    }

    [Fact]
    public async Task Dropping_below_twenty_percent_alerts_once_in_app_by_email_and_on_whatsapp()
    {
        await GrantAsync(1000);
        await SpendAsync(850, "m1");

        Assert.Equal(1, await _alerts.EvaluateAsync());
        Assert.Equal(0, await _alerts.EvaluateAsync());

        var alert = Assert.Single(await NotificationsAsync());
        Assert.Equal(TenantNotificationKind.QuotaLow20, alert.Kind);
        Assert.Equal(DeliveryStatus.Sent, alert.EmailStatus);
        Assert.Equal(DeliveryStatus.Sent, alert.WhatsAppStatus);
        Assert.Single(_email.Sent);
        var whatsApp = Assert.Single(_whatsApp.Sent);
        Assert.Equal("+919876543210", whatsApp.To);
        Assert.Equal("Acme", whatsApp.Parameters[0]);
        Assert.Contains("150 of 1,000", whatsApp.Parameters[1]);
    }

    [Fact]
    public async Task Each_deeper_threshold_alerts_once_more_and_running_out_says_so()
    {
        await GrantAsync(1000);
        await SpendAsync(850, "m1");
        await _alerts.EvaluateAsync();

        await SpendAsync(110, "m2");
        await _alerts.EvaluateAsync();
        await SpendAsync(40, "m3");
        await _alerts.EvaluateAsync();
        await _alerts.EvaluateAsync();

        var kinds = (await NotificationsAsync()).Select(n => n.Kind).ToList();
        Assert.Equal(new[] { TenantNotificationKind.QuotaLow20, TenantNotificationKind.QuotaLow5, TenantNotificationKind.QuotaExhausted }, kinds);
    }

    [Fact]
    public async Task A_top_up_starts_a_new_episode_so_running_low_again_alerts_again()
    {
        await GrantAsync(1000);
        await SpendAsync(900, "m1");
        await _alerts.EvaluateAsync();

        await GrantAsync(1000);
        Assert.Equal(0, await _alerts.EvaluateAsync());

        await SpendAsync(1000, "m2");
        Assert.Equal(1, await _alerts.EvaluateAsync());
        Assert.Equal(2, (await NotificationsAsync()).Count(n => n.Kind == TenantNotificationKind.QuotaLow20 || n.Kind == TenantNotificationKind.QuotaLow5));
    }

    [Fact]
    public async Task Channels_that_dont_apply_are_skipped_not_failed()
    {
        _tenant.BillingAlertWhatsAppEnabled = false;
        _tenant.BillingAlertEmail = null;
        await _db.SaveChangesAsync();
        await GrantAsync(1000);
        await SpendAsync(900, "m1");

        await _alerts.EvaluateAsync();

        var alert = Assert.Single(await NotificationsAsync());
        Assert.Equal(DeliveryStatus.Skipped, alert.EmailStatus);
        Assert.Equal(DeliveryStatus.Skipped, alert.WhatsAppStatus);
        Assert.Empty(_email.Sent);
        Assert.Empty(_whatsApp.Sent);
    }

    [Fact]
    public async Task An_unconfigured_platform_whatsapp_is_recorded_as_skipped_and_a_broken_email_as_failed()
    {
        _whatsApp.Configured = false;
        _email.Succeed = false;
        await GrantAsync(1000);
        await SpendAsync(900, "m1");

        await _alerts.EvaluateAsync();

        var alert = Assert.Single(await NotificationsAsync());
        Assert.Equal(DeliveryStatus.Skipped, alert.WhatsAppStatus);
        Assert.Equal(DeliveryStatus.Failed, alert.EmailStatus);
        Assert.Contains("smtp refused", alert.DeliveryNote);
    }

    [Fact]
    public async Task Purchased_credits_about_to_expire_are_flagged_but_far_off_ones_are_not()
    {
        var pack = new CreditPack { QuotaType = QuotaType.WhatsAppMessages, Name = "1,000", Units = 1000, PriceCents = 2000 };
        await _ledger.GrantCreditsAsync(_tenant.Id, pack, Guid.NewGuid(), _clock.UtcNow);
        Assert.Equal(0, await _alerts.EvaluateAsync());

        _clock.UtcNow = _clock.UtcNow.AddMonths(12).AddDays(-10);
        Assert.Equal(1, await _alerts.EvaluateAsync());
        _clock.UtcNow = _clock.UtcNow.AddDays(8);
        Assert.Equal(1, await _alerts.EvaluateAsync());

        var kinds = (await NotificationsAsync()).Select(n => n.Kind).ToList();
        Assert.Equal(new[] { TenantNotificationKind.CreditsExpiring14, TenantNotificationKind.CreditsExpiring3 }, kinds);
    }

    [Fact]
    public async Task Suspended_tenants_are_not_alerted()
    {
        await GrantAsync(1000);
        await SpendAsync(950, "m1");
        _tenant.Status = TenantStatus.Suspended;
        await _db.SaveChangesAsync();

        Assert.Equal(0, await _alerts.EvaluateAsync());
    }
}
