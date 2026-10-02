using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class TenantBillingNoticeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantBillingNoticeService _service;
    private readonly FakeEmail _email = new();
    private readonly FakePlatformWhatsApp _whatsApp = new();
    private readonly Tenant _tenant = new()
    {
        Name = "Acme", Slug = "acme", Status = TenantStatus.Active,
        BillingAlertEmail = "billing@acme.test", BillingAlertPhoneE164 = "+919876543210", BillingAlertWhatsAppEnabled = true
    };

    public TenantBillingNoticeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        // UserManager is only reached when a tenant has neither an alert email nor an owner - not exercised here.
        var notifier = new TenantNotifier(_db, null!, _email, _whatsApp, new FakeNotificationBroadcaster(), new FixedOptions<BillingAlertOptions>(new()), NullLogger<TenantNotifier>.Instance);
        _service = new TenantBillingNoticeService(_db, new TestClock(), notifier);

        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TenantNotification Row(string episode, bool acknowledged = false) => new()
    {
        TenantId = _tenant.Id,
        Kind = TenantNotificationKind.QuotaLow20,
        EpisodeKey = episode,
        Title = "t",
        Body = "b",
        AcknowledgedAtUtc = acknowledged ? DateTime.UtcNow : null,
    };

    private TenantNotification RowAt(int minutesAgo, string episode, string title = "t", bool acknowledged = false)
    {
        var row = Row(episode, acknowledged);
        row.Title = title;
        row.CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo);
        return row;
    }

    [Fact]
    public async Task History_pages_through_every_notification_newest_first_and_only_this_tenants()
    {
        var otherTenant = new Tenant { Name = "Other", Slug = "other", Status = TenantStatus.Active };
        _db.Tenants.Add(otherTenant);
        for (var i = 0; i < 60; i++)
            _db.TenantNotifications.Add(RowAt(i, $"ep{i}", $"notice {i}"));
        _db.TenantNotifications.Add(new TenantNotification { TenantId = otherTenant.Id, Kind = TenantNotificationKind.QuotaLow20, EpisodeKey = "x", Title = "not mine", Body = "b" });
        await _db.SaveChangesAsync();

        var first = await _service.GetHistoryAsync(_tenant.Id, new PagedRequest { Page = 1, PageSize = 25 }, unreadOnly: false);
        var last = await _service.GetHistoryAsync(_tenant.Id, new PagedRequest { Page = 3, PageSize = 25 }, unreadOnly: false);

        Assert.Equal(60, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal("notice 0", first.Items[0].Title);
        Assert.Equal(10, last.Items.Count);
        Assert.Equal("notice 59", last.Items[^1].Title);
        Assert.DoesNotContain(first.Items.Concat(last.Items), n => n.Title == "not mine");
    }

    [Fact]
    public async Task History_can_show_only_unread_and_search_the_title_or_body()
    {
        _db.TenantNotifications.Add(RowAt(1, "a", "Credits running low"));
        _db.TenantNotifications.Add(RowAt(2, "b", "Plan renewed", acknowledged: true));
        _db.TenantNotifications.Add(RowAt(3, "c", "Credits added"));
        await _db.SaveChangesAsync();

        var unread = await _service.GetHistoryAsync(_tenant.Id, new PagedRequest(), unreadOnly: true);
        var search = await _service.GetHistoryAsync(_tenant.Id, new PagedRequest { Search = "renewed" }, unreadOnly: false);

        Assert.Equal(2, unread.TotalCount);
        Assert.All(unread.Items, n => Assert.False(n.Acknowledged));
        Assert.Equal("Plan renewed", search.Items.Single().Title);
        Assert.True(search.Items.Single().Acknowledged);
    }

    [Fact]
    public async Task Acknowledge_all_clears_every_open_notification_for_this_tenant_only()
    {
        var otherTenant = new Tenant { Name = "Other", Slug = "other", Status = TenantStatus.Active };
        _db.Tenants.Add(otherTenant);
        _db.TenantNotifications.Add(Row("a"));
        _db.TenantNotifications.Add(Row("b"));
        _db.TenantNotifications.Add(new TenantNotification { TenantId = otherTenant.Id, Kind = TenantNotificationKind.QuotaLow20, EpisodeKey = "c", Title = "t", Body = "b" });
        await _db.SaveChangesAsync();

        await _service.AcknowledgeAllAsync(_tenant.Id);

        var mine = await _service.ListAsync(_tenant.Id);
        Assert.All(mine, n => Assert.True(n.Acknowledged));
        var theirs = await _service.ListAsync(otherTenant.Id);
        Assert.False(theirs.Single().Acknowledged);
    }

    [Fact]
    public async Task Delete_removes_the_row()
    {
        var row = Row("a");
        _db.TenantNotifications.Add(row);
        await _db.SaveChangesAsync();

        await _service.DeleteAsync(_tenant.Id, row.Id);

        Assert.Empty(await _service.ListAsync(_tenant.Id));
    }

    [Fact]
    public async Task Delete_of_another_tenants_notification_is_refused()
    {
        var otherTenant = new Tenant { Name = "Other", Slug = "other", Status = TenantStatus.Active };
        _db.Tenants.Add(otherTenant);
        var row = new TenantNotification { TenantId = otherTenant.Id, Kind = TenantNotificationKind.QuotaLow20, EpisodeKey = "a", Title = "t", Body = "b" };
        _db.TenantNotifications.Add(row);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => _service.DeleteAsync(_tenant.Id, row.Id));
    }

    [Fact]
    public async Task SendTestAsync_delivers_through_the_real_path_with_a_test_prefixed_title()
    {
        var sent = await _service.SendTestAsync(_tenant.Id, TenantNotificationKind.QuotaExhausted, QuotaType.LeadCandidates);

        Assert.StartsWith("[Test]", sent.Title);
        Assert.Equal(QuotaType.LeadCandidates, sent.QuotaType);
        Assert.Equal(DeliveryStatus.Sent, sent.EmailStatus);
        Assert.Equal(DeliveryStatus.Sent, sent.WhatsAppStatus);
        Assert.Single(_email.Sent);
        Assert.Single(_whatsApp.Sent);

        var listed = await _service.ListAsync(_tenant.Id);
        Assert.Contains(listed, n => n.Id == sent.Id);
    }

    [Fact]
    public async Task SendTestAsync_never_dedupes_against_an_earlier_test_or_itself()
    {
        var first = await _service.SendTestAsync(_tenant.Id, TenantNotificationKind.QuotaLow20, QuotaType.WhatsAppMessages);
        var second = await _service.SendTestAsync(_tenant.Id, TenantNotificationKind.QuotaLow20, QuotaType.WhatsAppMessages);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await _service.ListAsync(_tenant.Id)).Count);
    }

    [Fact]
    public async Task SendTestAsync_requires_a_quota_type_for_a_quota_threshold_kind()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => _service.SendTestAsync(_tenant.Id, TenantNotificationKind.QuotaExhausted, null));
    }

    [Fact]
    public async Task SendTestAsync_ignores_quota_type_for_a_non_quota_kind()
    {
        var sent = await _service.SendTestAsync(_tenant.Id, TenantNotificationKind.PlanExpiring1, QuotaType.WhatsAppMessages);

        Assert.Null(sent.QuotaType);
    }
}
