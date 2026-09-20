using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
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
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme", Status = TenantStatus.Active };

    public TenantBillingNoticeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _service = new TenantBillingNoticeService(_db, new TestClock());

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
}
