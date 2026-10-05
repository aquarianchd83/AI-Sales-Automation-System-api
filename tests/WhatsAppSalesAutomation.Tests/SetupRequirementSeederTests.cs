using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Setup;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The out-of-the-box requirement catalogues have to be internally sound: they are what every fresh
/// install's Talents see, and nobody reviews them again after the first boot.</summary>
public sealed class SetupRequirementSeederTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;

    public SetupRequirementSeederTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedAsync()
    {
        _db.Plans.AddRange(
            new Plan { Code = "starter", Name = "Starter", IsActive = true },
            new Plan { Code = "growth", Name = "Growth", IsActive = true },
            new Plan { Code = "scale", Name = "Scale", IsActive = true });
        await _db.SaveChangesAsync();
        await SetupRequirementSeeder.SeedAsync(new Services(_db));
    }

    [Fact]
    public async Task Each_starter_plan_gets_a_published_version_one_and_reseeding_changes_nothing()
    {
        await SeedAsync();
        var before = await _db.PlanRequirements.CountAsync();

        await SetupRequirementSeeder.SeedAsync(new Services(_db));

        Assert.Equal(3, await _db.PlanSetupVersions.CountAsync(v => v.Status == SetupVersionStatus.Published && v.VersionNumber == 1));
        Assert.Equal(before, await _db.PlanRequirements.CountAsync());
    }

    [Fact]
    public async Task A_plan_an_admin_already_set_up_is_never_reseeded()
    {
        var plan = new Plan { Code = "starter", Name = "Starter", IsActive = true };
        _db.Plans.Add(plan);
        _db.PlanSetupVersions.Add(new() { PlanId = plan.Id, VersionNumber = 1, Status = SetupVersionStatus.Draft });
        await _db.SaveChangesAsync();

        await SetupRequirementSeeder.SeedAsync(new Services(_db));

        Assert.Empty(await _db.PlanRequirements.ToListAsync());
    }

    [Fact]
    public async Task Every_seeded_requirement_is_coherent()
    {
        await SeedAsync();
        var versions = await _db.PlanSetupVersions.ToListAsync();

        foreach (var version in versions)
        {
            var fields = await _db.PlanRequirements.Where(r => r.PlanSetupVersionId == version.Id).ToListAsync();
            var byKey = fields.ToDictionary(f => f.FieldKey);
            Assert.Equal(fields.Count, byKey.Count); // unique keys

            foreach (var field in fields)
            {
                if (SaveRequirementRequestValidator.IsChoice(field.FieldType))
                    Assert.NotEmpty(SetupJson.ParseOptions(field.OptionsJson));

                if (field.ConditionFieldKey is not null)
                {
                    Assert.True(byKey.ContainsKey(field.ConditionFieldKey), $"{field.FieldKey} depends on missing {field.ConditionFieldKey}");
                }

                if (field.DefaultValue is not null)
                    Assert.Null(SetupEvaluator.ValidateValue(field, field.DefaultValue));

                if (field.MetricKey is not null)
                    Assert.Contains(field.MetricKey, SetupMetrics.All);
            }
        }
    }

    [Fact]
    public async Task Every_seeded_version_passes_the_same_publish_checks_the_admin_screen_applies()
    {
        await SeedAsync();
        var admin = new SetupAdminService(
            _db, new SaveRequirementRequestValidator(), new SaveSetupVersionRequestValidator(), new CreateSetupVersionRequestValidator(),
            new AnonymousUser(), new TestClock());

        foreach (var plan in await _db.Plans.ToListAsync())
        {
            var draft = await admin.CreateVersionAsync(plan.Id, new CreateSetupVersionRequest(null, null)); // a copy of the seeded fields
            var published = await admin.PublishVersionAsync(draft.Id);
            Assert.Equal(SetupVersionStatus.Published, published.Status);
            Assert.Equal(2, published.VersionNumber);
        }
    }

    [Fact]
    public async Task Growth_asks_everything_starter_does_plus_lead_generation_questions()
    {
        await SeedAsync();
        async Task<HashSet<string>> KeysAsync(string code)
        {
            var plan = await _db.Plans.SingleAsync(p => p.Code == code);
            var version = await _db.PlanSetupVersions.SingleAsync(v => v.PlanId == plan.Id);
            return (await _db.PlanRequirements.Where(r => r.PlanSetupVersionId == version.Id).Select(r => r.FieldKey).ToListAsync()).ToHashSet();
        }

        var starter = await KeysAsync("starter");
        var growth = await KeysAsync("growth");

        Assert.True(starter.IsSubsetOf(growth));
        Assert.Contains("lead_source", growth);
        Assert.DoesNotContain("lead_source", starter);
    }

    private sealed class Services : IServiceProvider
    {
        private readonly ApplicationDbContext _db;

        public Services(ApplicationDbContext db) => _db = db;

        public object? GetService(Type serviceType) => serviceType == typeof(ApplicationDbContext) ? _db : null;
    }
}
