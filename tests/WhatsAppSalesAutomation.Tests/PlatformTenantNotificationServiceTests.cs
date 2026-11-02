using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The platform's list of what tenants were told: one row per channel of each notification, across tenants, filterable by mode and status.</summary>
public sealed class PlatformTenantNotificationServiceTests : IDisposable
{
    private sealed class AmbientTenant : ITenantContext
    {
        public AmbientTenant(Guid tenantId) => TenantId = tenantId;

        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public PlatformTenantNotificationServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_a), new NoUser()) { StampTenantId = _a };
        _db.Database.EnsureCreated();
        _db.Tenants.AddRange(new Tenant { Id = _a, Name = "Alpha", Slug = "alpha" }, new Tenant { Id = _b, Name = "Beta", Slug = "beta" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private PlatformTenantNotificationService Service() => new(_db);

    private async Task AddAsync(Guid tenant, string title, DeliveryStatus email, DeliveryStatus whatsApp, string? note = null)
    {
        _db.TenantNotifications.Add(new TenantNotification
        {
            TenantId = tenant, Kind = TenantNotificationKind.QuotaLow20, EpisodeKey = Guid.NewGuid().ToString(), Title = title, Body = title + " body",
            EmailStatus = email, WhatsAppStatus = whatsApp, DeliveryNote = note
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task One_notification_is_a_row_per_channel_across_tenants()
    {
        await AddAsync(_a, "Low", DeliveryStatus.Sent, DeliveryStatus.Failed, "WhatsApp: token expired");
        await AddAsync(_b, "Plan ends", DeliveryStatus.Skipped, DeliveryStatus.NotAttempted);

        var page = await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 1, PageSize = 20 });

        Assert.Equal(6, page.TotalCount);
        Assert.Equal(new[] { "Alpha", "Beta" }, page.Items.Select(i => i.TenantName).Distinct().OrderBy(n => n));
        var alpha = page.Items.Where(i => i.TenantName == "Alpha").ToDictionary(i => i.Channel);
        Assert.Equal("Sent", alpha["InApp"].Status);
        Assert.Equal("Sent", alpha["Email"].Status);
        Assert.Equal("Failed", alpha["WhatsApp"].Status);
    }

    [Fact]
    public async Task Filters_by_mode_and_by_status()
    {
        await AddAsync(_a, "Low", DeliveryStatus.Sent, DeliveryStatus.Failed);
        await AddAsync(_b, "Plan ends", DeliveryStatus.NotAttempted, DeliveryStatus.NotAttempted);

        var whatsApp = await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 1, PageSize = 20, Channel = "whatsapp" });
        Assert.Equal(2, whatsApp.TotalCount);
        Assert.All(whatsApp.Items, i => Assert.Equal("WhatsApp", i.Channel));

        var pending = await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 1, PageSize = 20, Status = "Pending" });
        Assert.Equal(2, pending.TotalCount);
        Assert.All(pending.Items, i => Assert.Equal("Beta", i.TenantName));

        var failedEmail = await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 1, PageSize = 20, Channel = "Email", Status = "Failed" });
        Assert.Equal(0, failedEmail.TotalCount);
    }

    [Fact]
    public async Task The_delivery_note_shows_only_on_a_channel_that_did_not_go()
    {
        await AddAsync(_a, "Low", DeliveryStatus.Sent, DeliveryStatus.Failed, "WhatsApp: token expired");

        var items = (await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 1, PageSize = 20 })).Items.ToDictionary(i => i.Channel);

        Assert.Null(items["Email"].DeliveryNote);
        Assert.Equal("WhatsApp: token expired", items["WhatsApp"].DeliveryNote);
    }

    [Fact]
    public async Task Pages_and_narrows_to_one_tenant()
    {
        await AddAsync(_a, "One", DeliveryStatus.Sent, DeliveryStatus.Sent);
        await AddAsync(_b, "Two", DeliveryStatus.Sent, DeliveryStatus.Sent);

        var page = await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 2, PageSize = 2, TenantId = _a });

        Assert.Equal(3, page.TotalCount);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Filters_by_the_kind_of_notice_and_lists_every_kind()
    {
        await AddAsync(_a, "Low", DeliveryStatus.Sent, DeliveryStatus.Sent);
        _db.TenantNotifications.Add(new TenantNotification
        {
            TenantId = _a, Kind = TenantNotificationKind.NextStepBooked, EpisodeKey = "b", Title = "Booked", Body = "Demo", EmailStatus = DeliveryStatus.Skipped, WhatsAppStatus = DeliveryStatus.Failed
        });
        await _db.SaveChangesAsync();

        var booked = await Service().GetPagedAsync(new PlatformTenantNotificationQuery { Page = 1, PageSize = 20, Kind = "nextstepbooked" });

        Assert.Equal(3, booked.TotalCount);
        Assert.All(booked.Items, i => Assert.Equal("NextStepBooked", i.Kind));
        Assert.Contains("NextStepBooked", Service().GetKinds());
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().GetPagedAsync(new PlatformTenantNotificationQuery { Kind = "Nope" }));
    }

    [Fact]
    public async Task An_unknown_mode_or_status_is_refused()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().GetPagedAsync(new PlatformTenantNotificationQuery { Channel = "Fax" }));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Service().GetPagedAsync(new PlatformTenantNotificationQuery { Status = "Lost" }));
    }
}
