using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Retiring a campaign: kept as a record with the tenant's reason, closed to change, listed last.</summary>
public sealed class CampaignRetireTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly CampaignService _service;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };

    public CampaignRetireTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        _service = new CampaignService(_db, new Clock(), null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Campaign> SeedAsync(string name, CampaignStatus status, DateTime createdAt)
    {
        var campaign = new Campaign { Name = name, Status = status, CreatedBy = Guid.NewGuid() };
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync();
        campaign.CreatedAt = createdAt;
        await _db.SaveChangesAsync();
        return campaign;
    }

    [Fact]
    public async Task Retiring_records_the_reason_and_time_and_keeps_the_campaign()
    {
        var campaign = await SeedAsync("c", CampaignStatus.Draft, new DateTime(2026, 1, 1));

        var dto = await _service.RetireAsync(campaign.Id, new RetireCampaignRequest("  Wrong audience  "));

        Assert.Equal("Wrong audience", dto.RetiredReason);
        Assert.Equal(new DateTime(2026, 10, 15, 6, 0, 0), dto.RetiredAt);
        Assert.Equal(1, await _db.Campaigns.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_reason_is_required(string reason)
    {
        var campaign = await SeedAsync("c", CampaignStatus.Draft, new DateTime(2026, 1, 1));

        await Assert.ThrowsAsync<ValidationException>(() => _service.RetireAsync(campaign.Id, new RetireCampaignRequest(reason)));
        Assert.Null((await _db.Campaigns.SingleAsync()).RetiredAt);
    }

    [Fact]
    public async Task A_running_campaign_must_be_stopped_before_it_is_retired()
    {
        var campaign = await SeedAsync("c", CampaignStatus.Running, new DateTime(2026, 1, 1));

        await Assert.ThrowsAsync<ConflictException>(() => _service.RetireAsync(campaign.Id, new RetireCampaignRequest("done")));
    }

    [Fact]
    public async Task Retiring_twice_keeps_the_first_reason()
    {
        var campaign = await SeedAsync("c", CampaignStatus.Stopped, new DateTime(2026, 1, 1));
        await _service.RetireAsync(campaign.Id, new RetireCampaignRequest("first"));

        var again = await _service.RetireAsync(campaign.Id, new RetireCampaignRequest("second"));

        Assert.Equal("first", again.RetiredReason);
    }

    [Fact]
    public async Task A_retired_campaign_is_closed_to_changes()
    {
        var campaign = await SeedAsync("c", CampaignStatus.Draft, new DateTime(2026, 1, 1));
        await _service.RetireAsync(campaign.Id, new RetireCampaignRequest("done"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.PauseAsync(campaign.Id));
    }

    [Fact]
    public async Task Retired_campaigns_are_listed_last_even_when_newer()
    {
        var retired = await SeedAsync("retired-newest", CampaignStatus.Stopped, new DateTime(2026, 3, 1));
        await SeedAsync("active-old", CampaignStatus.Draft, new DateTime(2026, 1, 1));
        await SeedAsync("active-new", CampaignStatus.Draft, new DateTime(2026, 2, 1));
        await _service.RetireAsync(retired.Id, new RetireCampaignRequest("done"));

        var page = await _service.GetPagedAsync(new PagedRequest { Page = 1, PageSize = 10 });

        Assert.Equal(new[] { "active-new", "active-old", "retired-newest" }, page.Items.Select(c => c.Name));
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 10, 15, 6, 0, 0);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class AmbientTenant : ITenantContext
    {
        private readonly Guid _tenantId;
        public AmbientTenant(Guid tenantId) => _tenantId = tenantId;
        public Guid? TenantId => _tenantId;
        public bool IsPlatformSuperAdmin => false;
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
