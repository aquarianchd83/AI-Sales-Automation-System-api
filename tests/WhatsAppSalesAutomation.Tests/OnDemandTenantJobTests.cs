using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Some of a tenant's recurring jobs only mean something once it has set up what they work on: the four campaign
/// jobs (initial sends, follow-ups, retries, completion) need a campaign, and the lead discovery job needs a lead discovery
/// profile. Registering them at signup meant jobs that ran against nothing - and, for the self-service ones, announced
/// themselves every time. They are created with the thing they work on and removed if it goes.</summary>
public sealed class OnDemandTenantJobTests : IDisposable
{
    private static readonly string[] CampaignJobs =
    {
        TenantJobTypes.CampaignInitialSends, TenantJobTypes.CampaignFollowUps,
        TenantJobTypes.CampaignSendRetries, TenantJobTypes.CampaignCompletion,
    };

    private static readonly string[] AllJobs = TenantJobCatalog.All.Select(d => d.Key).OrderBy(x => x).ToArray();

    private static string[] Without(params string[][] groups) =>
        AllJobs.Where(k => !groups.Any(g => g.Contains(k))).ToArray();

    private static readonly string[] LeadJob = { TenantJobTypes.LeadDiscovery };

    private sealed class FakeScheduler : ITenantJobScheduler
    {
        public HashSet<string> Registered { get; } = new();

        public bool IsValidCron(string cronExpression) => true;

        public void AddOrUpdateTenantJob(Guid tenantId, string jobType, string cronExpression) => Registered.Add(TenantJobCatalog.RecurringJobId(jobType, tenantId));

        public void RemoveTenantJob(Guid tenantId, string jobType) => Registered.Remove(TenantJobCatalog.RecurringJobId(jobType, tenantId));

        public string TriggerTenantJob(Guid tenantId, string jobType) => "job";

        public IReadOnlyDictionary<string, RecurringJobRegistrationState> GetRegistrations(IEnumerable<string> recurringJobIds) =>
            new Dictionary<string, RecurringJobRegistrationState>();

        public IReadOnlyList<string> GetRegisteredTenantJobIds() => Registered.ToList();

        public IReadOnlyList<RecurringJobRegistrationState> GetPlatformJobs() => Array.Empty<RecurringJobRegistrationState>();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly FakeScheduler _scheduler = new();
    private readonly TenantJobProvisioner _provisioner;

    public OnDemandTenantJobTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _provisioner = new TenantJobProvisioner(_db, _scheduler, NullLogger<TenantJobProvisioner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> NewTenantAsync(string name, TenantStatus status = TenantStatus.Active)
    {
        var tenant = new Tenant { Name = name, Slug = name.ToLowerInvariant(), Status = status };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Campaign> AddCampaignAsync(Guid tenantId)
    {
        _db.StampTenantId = tenantId;
        var campaign = new Campaign { TenantId = tenantId, Name = "Launch", CreatedBy = Guid.NewGuid() };
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync();
        return campaign;
    }

    private async Task<LeadDiscoveryProfile> AddLeadProfileAsync(Guid tenantId)
    {
        _db.StampTenantId = tenantId;
        var profile = new LeadDiscoveryProfile { TenantId = tenantId, TargetBusinessType = "Eye clinic" };
        _db.LeadDiscoveryProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
    }

    private string[] RegisteredFor(Guid tenantId) =>
        _scheduler.Registered.Where(id => id.EndsWith($":{tenantId}")).Select(id => id[..id.IndexOf(':')]).OrderBy(x => x).ToArray();

    private async Task<string[]> RowsFor(Guid tenantId) =>
        (await _db.TenantJobSchedules.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).Select(s => s.JobType).ToListAsync()).OrderBy(x => x).ToArray();

    // ---- campaign jobs ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_tenant_gets_neither_the_campaign_jobs_nor_lead_discovery_at_registration()
    {
        var tenant = await NewTenantAsync("Fresh");

        await _provisioner.SyncTenantAsync(tenant);

        var expected = Without(CampaignJobs, LeadJob);
        Assert.Equal(expected, await RowsFor(tenant));
        Assert.Equal(expected, RegisteredFor(tenant));
    }

    [Fact]
    public async Task The_campaign_jobs_are_created_with_the_first_campaign()
    {
        var tenant = await NewTenantAsync("Active");
        await _provisioner.SyncTenantAsync(tenant);

        await AddCampaignAsync(tenant);
        await _provisioner.SyncTenantAsync(tenant);

        var expected = Without(LeadJob); // still no lead discovery profile
        Assert.Equal(expected, await RowsFor(tenant));
        Assert.Equal(expected, RegisteredFor(tenant));
    }

    [Fact]
    public async Task Deleting_the_last_campaign_takes_the_campaign_jobs_away_again()
    {
        var tenant = await NewTenantAsync("Active");
        var campaign = await AddCampaignAsync(tenant);
        await _provisioner.SyncTenantAsync(tenant);
        Assert.Subset(RegisteredFor(tenant).ToHashSet(), CampaignJobs.ToHashSet());

        _db.Campaigns.Remove(campaign);
        await _db.SaveChangesAsync();
        await _provisioner.SyncTenantAsync(tenant);

        Assert.DoesNotContain(RegisteredFor(tenant), CampaignJobs.Contains);
        Assert.DoesNotContain(await RowsFor(tenant), CampaignJobs.Contains);
    }

    [Fact]
    public async Task One_tenants_campaign_does_not_give_another_tenant_campaign_jobs()
    {
        var withCampaign = await NewTenantAsync("Busy");
        var without = await NewTenantAsync("Quiet");
        await AddCampaignAsync(withCampaign);

        await _provisioner.SyncTenantAsync(withCampaign);
        await _provisioner.SyncTenantAsync(without);

        Assert.Subset(RegisteredFor(withCampaign).ToHashSet(), CampaignJobs.ToHashSet());
        Assert.DoesNotContain(RegisteredFor(without), CampaignJobs.Contains);
    }

    // ---- lead discovery --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_lead_discovery_job_is_created_when_the_profile_is_first_saved()
    {
        var tenant = await NewTenantAsync("Hunter");
        await _provisioner.SyncTenantAsync(tenant);
        Assert.DoesNotContain(TenantJobTypes.LeadDiscovery, RegisteredFor(tenant));
        Assert.DoesNotContain(TenantJobTypes.LeadDiscovery, await RowsFor(tenant));

        await AddLeadProfileAsync(tenant);
        await _provisioner.SyncTenantAsync(tenant);

        Assert.Contains(TenantJobTypes.LeadDiscovery, RegisteredFor(tenant));
        Assert.Contains(TenantJobTypes.LeadDiscovery, await RowsFor(tenant));
        Assert.DoesNotContain(RegisteredFor(tenant), CampaignJobs.Contains); // a profile is not a campaign
    }

    [Fact]
    public async Task One_tenants_profile_does_not_give_another_tenant_the_lead_discovery_job()
    {
        var withProfile = await NewTenantAsync("Hunter");
        var without = await NewTenantAsync("Quiet");
        await AddLeadProfileAsync(withProfile);

        await _provisioner.SyncTenantAsync(withProfile);
        await _provisioner.SyncTenantAsync(without);

        Assert.Contains(TenantJobTypes.LeadDiscovery, RegisteredFor(withProfile));
        Assert.DoesNotContain(TenantJobTypes.LeadDiscovery, RegisteredFor(without));
    }

    [Fact]
    public async Task A_tenant_with_a_campaign_and_a_profile_has_every_job()
    {
        var tenant = await NewTenantAsync("Everything");
        await AddCampaignAsync(tenant);
        await AddLeadProfileAsync(tenant);

        await _provisioner.SyncTenantAsync(tenant);

        Assert.Equal(AllJobs, RegisteredFor(tenant));
        Assert.Equal(AllJobs, await RowsFor(tenant));
    }

    // ---- the daily reconcile -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_daily_reconcile_clears_the_idle_jobs_tenants_already_carry_and_leaves_the_rest()
    {
        var busy = await NewTenantAsync("Busy");        // has a campaign and a lead profile
        var quiet = await NewTenantAsync("Quiet");      // has neither
        var hunter = await NewTenantAsync("Hunter");    // has a lead profile only
        await AddCampaignAsync(busy);
        await AddLeadProfileAsync(busy);
        await AddLeadProfileAsync(hunter);

        // How every tenant looked before: every job scheduled and registered, whatever it had set up.
        foreach (var tenant in new[] { busy, quiet, hunter })
        {
            foreach (var definition in TenantJobCatalog.All)
            {
                _db.TenantJobSchedules.Add(new TenantJobSchedule { TenantId = tenant, JobType = definition.Key, CronExpression = definition.DefaultCron, IsEnabled = true });
                _scheduler.AddOrUpdateTenantJob(tenant, definition.Key, definition.DefaultCron);
            }
        }
        await _db.SaveChangesAsync();

        await _provisioner.ReconcileAllAsync();

        Assert.Equal(AllJobs, RegisteredFor(busy));
        Assert.Equal(AllJobs, await RowsFor(busy));
        Assert.Equal(Without(CampaignJobs, LeadJob), RegisteredFor(quiet));
        Assert.Equal(Without(CampaignJobs, LeadJob), await RowsFor(quiet));
        Assert.Equal(Without(CampaignJobs), RegisteredFor(hunter));
        Assert.Equal(Without(CampaignJobs), await RowsFor(hunter));
    }

    [Fact]
    public async Task A_second_reconcile_changes_nothing()
    {
        var quiet = await NewTenantAsync("Quiet");
        await _provisioner.ReconcileAllAsync();
        var rows = await RowsFor(quiet);
        var registered = RegisteredFor(quiet);

        var again = await _provisioner.ReconcileAllAsync();

        Assert.Equal(0, again.SchedulesCreated);
        Assert.Equal(rows, await RowsFor(quiet));
        Assert.Equal(registered, RegisteredFor(quiet));
    }

    [Fact]
    public async Task A_suspended_tenant_is_still_given_nothing_to_run_even_with_a_campaign_and_a_profile()
    {
        var tenant = await NewTenantAsync("Paused", TenantStatus.Suspended);
        await AddCampaignAsync(tenant);
        await AddLeadProfileAsync(tenant);

        await _provisioner.SyncTenantAsync(tenant);

        Assert.Empty(RegisteredFor(tenant));
    }

    // ---- the catalog -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_catalog_says_which_jobs_wait_for_what()
    {
        Assert.Equal(CampaignJobs.OrderBy(x => x), TenantJobCatalog.NeedsCampaignKeys.OrderBy(x => x));
        Assert.Equal(LeadJob, TenantJobCatalog.NeedsLeadProfileKeys);
        Assert.All(CampaignJobs, key => Assert.True(TenantJobCatalog.IsKnown(key)));
        Assert.False(TenantJobCatalog.NeedsCampaign(TenantJobTypes.LeadFollowUps));
        Assert.False(TenantJobCatalog.NeedsLeadProfile(TenantJobTypes.LeadFollowUps));

        Assert.False(TenantJobCatalog.IsApplicable(TenantJobTypes.LeadDiscovery, hasCampaign: true, hasLeadProfile: false));
        Assert.True(TenantJobCatalog.IsApplicable(TenantJobTypes.LeadDiscovery, hasCampaign: false, hasLeadProfile: true));
        Assert.False(TenantJobCatalog.IsApplicable(TenantJobTypes.CampaignFollowUps, hasCampaign: false, hasLeadProfile: true));
        Assert.True(TenantJobCatalog.IsApplicable(TenantJobTypes.WhatsAppTemplateSync, hasCampaign: false, hasLeadProfile: false));
    }
}
