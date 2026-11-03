using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Veo switch under Integrations > Story Studio: what tenants may choose, and that it is enforced on the server and not only hidden in the page.</summary>
public sealed class StoryVideoSourceTests : IDisposable
{
    private sealed class Options : IOptionsSnapshot<CinematicOptions>
    {
        public CinematicOptions Value { get; init; } = new();
        public CinematicOptions Get(string? name) => Value;
    }

    private sealed class Keys : IPlatformAiConfigProvider
    {
        public string? Google { get; init; }
        public bool GoogleOn { get; init; } = true;

        public AiCredentials Get() => new("Simulated", "Simulated", null, "m", "v", "u", null, "m", "e", "u", Google, "m", "e", "u", GoogleEnabled: GoogleOn);
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

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class Metering : IStudioMeteringService
    {
        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new StudioChargeResult(true, Guid.NewGuid(), false, null));
        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class NoAi : IAiTaskRunner
    {
        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Store : ITenantDocumentStore
    {
        public bool IsConfigured => true;
        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(relativePath);
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private sealed class NoQueue : IStoryClipQueue
    {
        public int Enqueued { get; private set; }
        public void Enqueue(Guid jobId, Guid tenantId, TimeSpan? delay = null) => Enqueued++;
    }

    private sealed class FakeVeo : ISceneVideoProvider
    {
        public SceneVideoProviderKind Kind => SceneVideoProviderKind.Veo;
        public decimal? EstimateCostUsd(int seconds) => 0.12m * seconds;
        public Task<SceneClipResult> SubmitAsync(SceneClipRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new SceneClipResult(SceneClipState.Running, "op", null, null, null));
        public Task<SceneClipResult> PollAsync(string providerJobId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly NoQueue _queue = new();
    private readonly Guid _tenant = Guid.NewGuid();

    public StoryVideoSourceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();
        _config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static StoryVideoSourcePolicy Policy(string platform, string? googleKey, bool googleOn = true) =>
        new(new Options { Value = new CinematicOptions { VideoProvider = platform } }, new Keys { Google = googleKey, GoogleOn = googleOn });

    private StoryStudioService Studio(IStoryVideoSourcePolicy policy) => new(_db, _config, new NoAi(), new Metering(), new Store(),
        new SceneVideoProviderFactory(new ISceneVideoProvider[] { new FlowImportSceneVideoProvider(), new FakeVeo() }), new Clock(), policy);

    private StoryClipService Clips(IStoryVideoSourcePolicy policy) => new(_db, _config,
        new SceneVideoProviderFactory(new ISceneVideoProvider[] { new FlowImportSceneVideoProvider(), new FakeVeo() }), new Metering(), new Store(),
        Fake.Of<IFfmpegService>((m, a) => null), _queue, new Clock(), NullLogger<StoryClipService>.Instance, policy);

    private static CreateStoryProjectRequest Request(SceneVideoProviderKind kind) =>
        new("Title", "Topic", "Hinglish", null, null, null, 30, kind, null, Array.Empty<Guid>());

    [Fact]
    public void The_free_clips_and_flow_are_always_open_and_veo_needs_the_switch_and_a_gemini_key()
    {
        string? Reason(StoryVideoSourcePolicy p) => p.Sources().Single(s => s.Kind == SceneVideoProviderKind.Veo).Reason;
        bool Open(StoryVideoSourcePolicy p, SceneVideoProviderKind k) => p.Sources().Single(s => s.Kind == k).Available;

        var off = Policy("Simulator", "g");
        Assert.True(Open(off, SceneVideoProviderKind.Simulator));
        Assert.True(Open(off, SceneVideoProviderKind.FlowImport));
        Assert.False(Open(off, SceneVideoProviderKind.Veo));
        Assert.Contains("not switched on", Reason(off));
        Assert.Contains("Gemini", Reason(off));

        var noKey = Policy("Google", null);
        Assert.False(Open(noKey, SceneVideoProviderKind.Veo));
        Assert.Contains("Configuration > AI Providers", Reason(noKey));

        Assert.False(Open(Policy("Google", "g", googleOn: false), SceneVideoProviderKind.Veo));   // Gemini switched off there
        Assert.True(Open(Policy("Google", "g"), SceneVideoProviderKind.Veo));
        Assert.Null(Reason(Policy("Google", "g")));
    }

    [Fact]
    public async Task A_story_cannot_choose_veo_while_the_platform_has_it_off_but_can_choose_the_others()
    {
        var studio = Studio(Policy("Simulator", "g"));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => studio.CreateProjectAsync(_tenant, null, Request(SceneVideoProviderKind.Veo)));
        Assert.Contains("not switched on", ex.Message);
        Assert.Empty(await _db.StoryProjects.IgnoreQueryFilters().ToListAsync());

        await studio.CreateProjectAsync(_tenant, null, Request(SceneVideoProviderKind.Simulator));
        var flow = await studio.CreateProjectAsync(_tenant, null, Request(SceneVideoProviderKind.FlowImport));
        await Assert.ThrowsAsync<ConflictException>(() => studio.SetVideoProviderAsync(_tenant, flow.Id, SceneVideoProviderKind.Veo));
    }

    [Fact]
    public async Task With_veo_switched_on_and_a_key_a_story_can_choose_it()
    {
        var studio = Studio(Policy("Google", "g"));

        var project = await studio.CreateProjectAsync(_tenant, null, Request(SceneVideoProviderKind.Veo));

        Assert.Equal(SceneVideoProviderKind.Veo, project.VideoProvider);
    }

    [Fact]
    public async Task A_story_that_chose_veo_earlier_cannot_start_clips_after_the_platform_switches_it_off()
    {
        var project = await Studio(Policy("Google", "g")).CreateProjectAsync(_tenant, null, Request(SceneVideoProviderKind.Veo));
        var stored = await _db.StoryProjects.IgnoreQueryFilters().SingleAsync();
        var script = new StoryScriptVersion { TenantId = _tenant, ProjectId = project.Id, Number = 1, Json = "{}", ContentHash = "h", Source = "Simulator", Status = StoryScriptStatus.Approved };
        _db.StoryScriptVersions.Add(script);
        stored.Status = StoryProjectStatus.ScriptApproved;
        stored.CurrentScriptVersionId = script.Id;
        _db.StoryScenes.Add(new StoryScene { TenantId = _tenant, ProjectId = project.Id, ScriptVersionId = script.Id, Index = 1, TargetSeconds = 8, VideoPrompt = "p", Status = StorySceneStatus.Pending });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => Clips(Policy("Simulator", "g")).GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true)));
        Assert.Equal(0, _queue.Enqueued);
        Assert.Empty(await _db.StoryGenerationJobs.IgnoreQueryFilters().ToListAsync());

        await Clips(Policy("Google", "g")).GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true));
        Assert.Equal(1, _queue.Enqueued);
    }
}
