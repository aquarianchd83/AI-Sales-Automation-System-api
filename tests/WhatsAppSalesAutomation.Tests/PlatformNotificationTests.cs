using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class PlatformNotificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly PlatformNotificationService _service;

    public PlatformNotificationTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _service = new PlatformNotificationService(_db, new TestClock());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(TenantJobTypes.WhatsAppTokenRefresh, 1)]
    [InlineData(TenantJobTypes.LeadDiscovery, 1)]
    [InlineData(TenantJobTypes.CampaignInitialSends, 3)]
    public void Daily_jobs_alert_on_the_first_failure_frequent_ones_need_a_streak(string jobType, int expected)
        => Assert.Equal(expected, PlatformJobAlertRules.FailureThreshold(jobType));

    [Fact]
    public async Task Unacknowledged_alerts_list_first_and_carry_the_tenant_name()
    {
        var tenant = new Tenant { Name = "Acme", Slug = "acme", Status = TenantStatus.Active };
        _db.Tenants.Add(tenant);
        _db.PlatformNotifications.Add(Row(tenant.Id, "old-open"));
        _db.PlatformNotifications.Add(Row(tenant.Id, "done", acknowledged: true));
        await _db.SaveChangesAsync();

        var list = await _service.GetRecentAsync();

        Assert.Equal(2, list.Count);
        Assert.False(list[0].Acknowledged);
        Assert.True(list[1].Acknowledged);
        Assert.Equal("Acme", list[0].TenantName);
    }

    [Fact]
    public async Task Acknowledge_all_clears_every_open_alert()
    {
        _db.PlatformNotifications.Add(Row(null, "a"));
        _db.PlatformNotifications.Add(Row(null, "b"));
        await _db.SaveChangesAsync();

        await _service.AcknowledgeAllAsync();

        Assert.All(await _service.GetRecentAsync(), n => Assert.True(n.Acknowledged));
    }

    [Fact]
    public async Task Delete_removes_the_row_and_ignores_an_unknown_id()
    {
        var row = Row(null, "a");
        _db.PlatformNotifications.Add(row);
        await _db.SaveChangesAsync();

        await _service.DeleteAsync(row.Id);
        await _service.DeleteAsync(Guid.NewGuid());

        Assert.Empty(await _service.GetRecentAsync());
    }

    [Fact]
    public async Task The_same_episode_cannot_be_stored_twice()
    {
        var tenantId = Guid.NewGuid();
        _db.PlatformNotifications.Add(Row(tenantId, "ep1"));
        await _db.SaveChangesAsync();

        _db.PlatformNotifications.Add(Row(tenantId, "ep1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    private static PlatformNotification At(int minutesAgo, string episode, string title = "t", Guid? tenantId = null, bool acknowledged = false)
    {
        var row = Row(tenantId, episode, acknowledged);
        row.Title = title;
        row.CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo);
        return row;
    }

    [Fact]
    public async Task History_is_every_alert_newest_first_not_capped_at_the_bells_fifty()
    {
        for (var i = 0; i < 70; i++)
            _db.PlatformNotifications.Add(At(minutesAgo: i, episode: $"ep{i}", title: $"alert {i}"));
        await _db.SaveChangesAsync();

        var first = await _service.GetHistoryAsync(new PagedRequest { Page = 1, PageSize = 50 }, unreadOnly: false);
        var second = await _service.GetHistoryAsync(new PagedRequest { Page = 2, PageSize = 50 }, unreadOnly: false);

        Assert.Equal(70, first.TotalCount);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(20, second.Items.Count);
        Assert.Equal("alert 0", first.Items[0].Title);
        Assert.Equal("alert 69", second.Items[^1].Title);
        Assert.Equal(70, first.Items.Concat(second.Items).Select(n => n.Id).Distinct().Count());
    }

    [Fact]
    public async Task History_can_show_only_unread_and_search_title_body_or_tenant_name()
    {
        var acme = new Tenant { Name = "Acme Traders", Slug = "acme", Status = TenantStatus.Active };
        _db.Tenants.Add(acme);
        _db.PlatformNotifications.Add(At(1, "a", "Campaign sends failing", acme.Id));
        _db.PlatformNotifications.Add(At(2, "b", "Token refresh failing", null, acknowledged: true));
        _db.PlatformNotifications.Add(At(3, "c", "Lead discovery failing", null));
        await _db.SaveChangesAsync();

        var unread = await _service.GetHistoryAsync(new PagedRequest(), unreadOnly: true);
        var byTitle = await _service.GetHistoryAsync(new PagedRequest { Search = "token" }, unreadOnly: false);
        var byTenant = await _service.GetHistoryAsync(new PagedRequest { Search = "acme" }, unreadOnly: false);

        Assert.Equal(2, unread.TotalCount);
        Assert.All(unread.Items, n => Assert.False(n.Acknowledged));
        Assert.Equal("Token refresh failing", byTitle.Items.Single().Title);
        Assert.Equal("Campaign sends failing", byTenant.Items.Single().Title);
        Assert.Equal("Acme Traders", byTenant.Items.Single().TenantName);
    }

    private static PlatformNotification Row(Guid? tenantId, string episode, bool acknowledged = false) => new()
    {
        Kind = PlatformNotificationKind.JobFailing,
        Severity = PlatformNotificationSeverity.Critical,
        TenantId = tenantId,
        JobType = TenantJobTypes.CampaignInitialSends,
        EpisodeKey = episode,
        Title = "t",
        Body = "b",
        AcknowledgedAtUtc = acknowledged ? DateTime.UtcNow : null
    };
}
