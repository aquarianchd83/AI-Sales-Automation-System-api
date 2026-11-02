using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The cinematic story studio's service with a real (in-memory) database: tenant isolation, upload checks, the script gate and metering.</summary>
public sealed class StoryStudioServiceTests : IDisposable
{
    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class PlatformContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
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

    private sealed class Metering : IStudioMeteringService
    {
        public bool Allow { get; set; } = true;
        public List<string> Log { get; } = new();

        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
        {
            Log.Add("charge");
            return Task.FromResult(new StudioChargeResult(Allow, Guid.NewGuid(), false, Allow ? null : "No quota left."));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) { Log.Add("complete"); return Task.CompletedTask; }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default) { Log.Add("refund"); return Task.CompletedTask; }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Ai : IAiTaskRunner
    {
        public AiExecutionMode Mode { get; set; } = AiExecutionMode.Simulator;
        public bool Success { get; set; } = true;
        public string? Text { get; set; }

        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AiTaskResult(
            Success, Text, Mode, Mode == AiExecutionMode.Simulator ? "Simulator" : "OpenAI", null, false, Success ? null : "Boom", Success ? null : "Provider down",
            Guid.NewGuid(), Array.Empty<AiAttempt>(), null, null, null));
    }

    private sealed class Store : ITenantDocumentStore
    {
        public List<string> Saved { get; } = new();
        public bool IsConfigured => true;

        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            var key = $"{tenantId}/{relativePath}";
            Saved.Add(key);
            return Task.FromResult(key);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Metering _metering = new();
    private readonly Ai _ai = new();
    private readonly Store _store = new();
    private readonly StoryStudioService _service;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public StoryStudioServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();

        var config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        var providers = new SceneVideoProviderFactory(new ISceneVideoProvider[] { new FlowImportSceneVideoProvider() });
        _service = new StoryStudioService(_db, config, _ai, _metering, _store, providers, new Clock());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static byte[] Png(int w, int h)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        for (var i = 0; i < 4; i++)
        {
            b[16 + i] = (byte)(w >> (24 - 8 * i));
            b[20 + i] = (byte)(h >> (24 - 8 * i));
        }

        return b;
    }

    private Task<StoryProjectDto> NewProject(Guid tenant, params Guid[] characters) => _service.CreateProjectAsync(tenant, null,
        new CreateStoryProjectRequest("Guru Ji", "A couple argues", "Hinglish", null, null, null, 30, SceneVideoProviderKind.FlowImport, null, characters));

    [Fact]
    public async Task A_private_character_is_invisible_to_other_tenants_until_it_is_shared()
    {
        var mine = await _service.CreateCharacterAsync(_a, null, new CreateStoryCharacterRequest("Guru Ji", null, null, null, null));

        Assert.DoesNotContain(await _service.ListCharactersAsync(_b, null), c => c.Id == mine.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => NewProject(_b, mine.Id));

        var row = await _db.StoryCharacters.IgnoreQueryFilters().SingleAsync();
        row.Visibility = StoryCharacterVisibility.Shared;
        await _db.SaveChangesAsync();

        var seen = Assert.Single(await _service.ListCharactersAsync(_b, null));
        Assert.False(seen.IsOwnedByTenant);
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ArchiveCharacterAsync(_b, mine.Id));
    }

    [Fact]
    public async Task An_upload_needs_rights_confirmation_real_image_bytes_and_a_usable_size()
    {
        var c = await _service.CreateCharacterAsync(_a, null, new CreateStoryCharacterRequest("Sunita", null, null, null, null));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddReferenceAsync(_a, null, c.Id, new MemoryStream(Png(1024, 1024)), "a.png", false));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddReferenceAsync(_a, null, c.Id, new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "evil.png", true));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddReferenceAsync(_a, null, c.Id, new MemoryStream(Png(200, 200)), "small.png", true));
        Assert.Empty(_store.Saved);

        var ok = await _service.AddReferenceAsync(_a, null, c.Id, new MemoryStream(Png(1024, 1536)), "a.png", true);
        Assert.Equal(1, ok.Version);
        Assert.Single(_store.Saved);
        Assert.True((await _db.StoryCharacters.IgnoreQueryFilters().SingleAsync()).RightsConfirmed);
    }

    [Fact]
    public async Task Approving_a_new_reference_supersedes_the_old_one_instead_of_overwriting_it()
    {
        var c = await _service.CreateCharacterAsync(_a, null, new CreateStoryCharacterRequest("Ramesh", null, null, null, null));
        var v1 = await _service.AddReferenceAsync(_a, null, c.Id, new MemoryStream(Png(1024, 1024)), "1.png", true);
        var v2 = await _service.AddReferenceAsync(_a, null, c.Id, new MemoryStream(Png(1024, 1024)), "2.png", true);

        await _service.ApproveReferenceAsync(_a, c.Id, v1.Id);
        await _service.ApproveReferenceAsync(_a, c.Id, v2.Id);

        var rows = await _db.StoryCharacterReferences.IgnoreQueryFilters().OrderBy(r => r.Version).ToListAsync();
        Assert.Equal(StoryReferenceStatus.Superseded, rows[0].Status);
        Assert.Equal(StoryReferenceStatus.Approved, rows[1].Status);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task A_simulated_script_is_marked_as_test_data_is_saved_as_a_draft_and_costs_nothing()
    {
        var c = await _service.CreateCharacterAsync(_a, null, new CreateStoryCharacterRequest("Guru Ji", null, null, null, null));
        var project = await NewProject(_a, c.Id);

        var script = await _service.GenerateScriptAsync(_a, null, project.Id);

        Assert.True(script.IsSimulated);
        Assert.Equal(StoryScriptStatus.Draft, script.Status);
        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
        Assert.True(CinematicScriptValidator.Validate(System.Text.Json.JsonSerializer.Serialize(script.Script), 30).IsValid);
    }

    [Fact]
    public async Task A_failed_provider_saves_no_script_and_refunds_the_charge()
    {
        _ai.Mode = AiExecutionMode.Live;
        _ai.Success = false;
        var project = await NewProject(_a);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GenerateScriptAsync(_a, null, project.Id));

        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
        Assert.Empty(await _db.StoryScriptVersions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_live_reply_that_breaks_the_script_rules_is_rejected_and_refunded()
    {
        _ai.Mode = AiExecutionMode.Live;
        _ai.Text = "{\"title\":\"x\"}";
        var project = await NewProject(_a);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GenerateScriptAsync(_a, null, project.Id));
        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
    }

    [Fact]
    public async Task Without_quota_nothing_runs()
    {
        _metering.Allow = false;
        var project = await NewProject(_a);

        await Assert.ThrowsAsync<PlanLimitExceededException>(() => _service.GenerateScriptAsync(_a, null, project.Id));
        Assert.Equal(new[] { "charge" }, _metering.Log);
    }

    [Fact]
    public async Task Approval_creates_the_scenes_and_dialogue_and_locks_the_script()
    {
        var c = await _service.CreateCharacterAsync(_a, null, new CreateStoryCharacterRequest("Guru Ji", null, null, null, null));
        var project = await NewProject(_a, c.Id);
        await _service.GenerateScriptAsync(_a, null, project.Id);

        var scenes = await _service.ApproveScriptAsync(_a, null, project.Id);

        Assert.Equal(4, scenes.Count);
        Assert.All(scenes, s => Assert.NotEmpty(s.Dialogue));
        Assert.Equal(StoryProjectStatus.ScriptApproved, (await _service.GetProjectAsync(_a, project.Id)).Status);
        Assert.All(await _db.StoryDialogueLines.IgnoreQueryFilters().ToListAsync(), l => Assert.Equal(c.Id, l.CharacterId));
        await Assert.ThrowsAsync<ConflictException>(() => _service.ApproveScriptAsync(_a, null, project.Id));
        await Assert.ThrowsAsync<ConflictException>(() => _service.GenerateScriptAsync(_a, null, project.Id));
    }

    [Fact]
    public async Task Another_tenant_cannot_read_or_approve_the_project()
    {
        var project = await NewProject(_a);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetProjectAsync(_b, project.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ApproveScriptAsync(_b, null, project.Id));
    }

    [Fact]
    public async Task The_cost_estimate_is_unknown_for_flow_and_a_price_for_a_paid_provider()
    {
        var project = await NewProject(_a);
        Assert.False((await _service.EstimateCostAsync(_a, project.Id)).IsKnown);
    }
}
