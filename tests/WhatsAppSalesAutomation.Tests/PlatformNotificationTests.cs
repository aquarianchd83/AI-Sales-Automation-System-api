using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
