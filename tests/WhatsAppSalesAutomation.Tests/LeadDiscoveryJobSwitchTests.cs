using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Lead discovery has no job on/off switch of its own: the old schedule row's flag must not silence a Run now or a slot's run.</summary>
public sealed class LeadDiscoveryJobSwitchTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ServiceProvider _services;
    private readonly Guid _tenant = Guid.NewGuid();

    public LeadDiscoveryJobSwitchTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using (var seed = Context())
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new Tenant { Id = _tenant, Name = "Eye", Slug = "eye", Status = TenantStatus.Active });
            seed.TenantJobSchedules.Add(new TenantJobSchedule { TenantId = _tenant, JobType = TenantJobTypes.LeadDiscovery, CronExpression = "0 0 * * *", IsEnabled = false });
            seed.TenantJobSchedules.Add(new TenantJobSchedule { TenantId = _tenant, JobType = TenantJobTypes.CampaignInitialSends, CronExpression = "* * * * *", IsEnabled = false });
            seed.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddScoped<IApplicationDbContext>(_ => Context());
        services.AddScoped<ITenantContext>(_ => new Ambient(_tenant));
        services.AddScoped(_ => Stub<ITenantNotifier>.Create(new() { ["NotifyAsync"] = _ => Task.FromResult(true) }));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private SqliteApplicationDbContext Context() => new(_options, new Ambient(null), new AnonymousUser());

    private TenantJobRunner Runner() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), new TestClock(), NullLogger<TenantJobRunner>.Instance);

    [Fact]
    public async Task Lead_discovery_runs_even_though_its_old_schedule_row_is_switched_off()
    {
        var ran = false;
        await Runner().RunAsync(_tenant, TenantJobTypes.LeadDiscovery, (_, _) => { ran = true; return Task.FromResult<string?>("ok"); });

        Assert.True(ran);
    }

    [Fact]
    public async Task Other_jobs_still_honour_their_switch()
    {
        var ran = false;
        await Runner().RunAsync(_tenant, TenantJobTypes.CampaignInitialSends, (_, _) => { ran = true; return Task.FromResult<string?>("ok"); });

        Assert.False(ran);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid? tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
