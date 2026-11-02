using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

/// <summary>Clip generation with a real in-memory database and, where available, the real ffmpeg. Providers that cost money are replaced by a fake that counts calls.</summary>
public sealed class StoryClipServiceTests : IDisposable
{
    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
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
        public List<string> Log { get; } = new();

        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
        {
            Log.Add("charge");
            return Task.FromResult(new StudioChargeResult(true, Guid.NewGuid(), false, null));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) { Log.Add("complete"); return Task.CompletedTask; }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default) { Log.Add("refund"); return Task.CompletedTask; }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Store : ITenantDocumentStore
    {
        public List<string> Saved { get; } = new();
        public bool IsConfigured => true;

        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Saved.Add($"{tenantId}/{relativePath}");
            return Task.FromResult(Saved[^1]);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private sealed class Queue : IStoryClipQueue
    {
        public List<(Guid Job, TimeSpan? Delay)> Items { get; } = new();

        public void Enqueue(Guid jobId, Guid tenantId, TimeSpan? delay = null) => Items.Add((jobId, delay));
    }

    /// <summary>A paid provider: counts submissions, and answers polls from a script.</summary>
    private sealed class PaidProvider : ISceneVideoProvider
    {
        public int Submits { get; private set; }
        public Queue<SceneClipResult> Polls { get; } = new();
        public SceneClipResult SubmitResult { get; set; } = new(SceneClipState.Running, "op-1", null, 0.96m, null);
        public SceneVideoProviderKind Kind => SceneVideoProviderKind.Veo;
        public decimal? EstimateCostUsd(int seconds) => 0.12m * seconds;

        public Task<SceneClipResult> SubmitAsync(SceneClipRequest request, CancellationToken cancellationToken = default)
        {
            Submits++;
            return Task.FromResult(SubmitResult);
        }

        public Task<SceneClipResult> PollAsync(string providerJobId, CancellationToken cancellationToken = default) => Task.FromResult(Polls.Dequeue());
    }

    private sealed class Snapshot : IOptionsSnapshot<CinematicOptions>
    {
        public CinematicOptions Value { get; } = new()
        {
            FfmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? string.Empty,
            FfprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? string.Empty
        };

        public CinematicOptions Get(string? name) => Value;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Metering _metering = new();
    private readonly Store _store = new();
    private readonly Queue _queue = new();
    private readonly PaidProvider _paid = new();
    private readonly FfmpegService _ffmpeg = new(new Snapshot());
    private readonly Clock _clock = new();
    private readonly StoryClipService _service;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly List<string> _temp = new();

    public StoryClipServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();

        var config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        var providers = new SceneVideoProviderFactory(new ISceneVideoProvider[]
        {
            new SimulatorSceneVideoProvider(_ffmpeg), new FlowImportSceneVideoProvider(), _paid
        });
        _service = new StoryClipService(_db, config, providers, _metering, _store, _ffmpeg, _queue, _clock, NullLogger<StoryClipService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        foreach (var path in _temp) if (File.Exists(path)) File.Delete(path);
    }

    private StoryProject AddProject(SceneVideoProviderKind kind, StoryProjectStatus status = StoryProjectStatus.ScriptApproved, decimal? budget = null, int scenes = 2)
    {
        var script = new StoryScriptVersion { TenantId = _tenant, Number = 1, Json = "{}", ContentHash = "h", Source = "Simulator", Status = StoryScriptStatus.Approved };
        var project = new StoryProject
        {
            TenantId = _tenant, Title = "Guru Ji", Topic = "t", Language = "Hinglish", Status = status, VideoProvider = kind,
            BudgetUsd = budget, CurrentScriptVersionId = script.Id
        };
        script.ProjectId = project.Id;
        _db.StoryProjects.Add(project);
        _db.StoryScriptVersions.Add(script);
        for (var i = 1; i <= scenes; i++)
            _db.StoryScenes.Add(new StoryScene { TenantId = _tenant, ProjectId = project.Id, ScriptVersionId = script.Id, Index = i, TargetSeconds = 8, VideoPrompt = $"p{i}", Status = StorySceneStatus.Pending });
        _db.SaveChanges();
        return project;
    }

    private async Task<string> MakeClipAsync(int seconds, bool vertical = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.mp4");
        _temp.Add(path);
        await _ffmpeg.CreateTestClipAsync(path, seconds, "t");
        if (!vertical)
        {
            var wide = Path.Combine(Path.GetTempPath(), $"wide-{Guid.NewGuid():N}.mp4");
            _temp.Add(wide);
            var (code, _, _) = (0, "", "");
            await RunFfmpegAsync("-y", "-f", "lavfi", "-i", $"color=c=blue:s=1280x720:d={seconds}", "-f", "lavfi", "-i", "sine=duration=" + seconds, "-shortest", wide);
            _ = code;
            return wide;
        }

        return path;
    }

    private static async Task RunFfmpegAsync(params string[] args)
    {
        var exe = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        var info = new System.Diagnostics.ProcessStartInfo(string.IsNullOrEmpty(exe) ? "ffmpeg" : exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(info)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
    }

    private async Task<bool> HaveFfmpegAsync() => await _ffmpeg.IsAvailableAsync();

    [Fact]
    public async Task Clips_cannot_start_until_the_script_is_approved()
    {
        var project = AddProject(SceneVideoProviderKind.Simulator, StoryProjectStatus.Draft);
        await Assert.ThrowsAsync<ConflictException>(() => _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false)));
    }

    [Fact]
    public async Task A_paid_provider_needs_the_cost_confirmed_and_submits_nothing_until_it_is()
    {
        var project = AddProject(SceneVideoProviderKind.Veo);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false)));
        Assert.Contains("1.92", ex.Message);
        Assert.Empty(_queue.Items);
        Assert.Empty(_metering.Log);
        Assert.Empty(await _db.StoryGenerationJobs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_confirmed_paid_run_charges_and_queues_one_job_per_scene()
    {
        var project = AddProject(SceneVideoProviderKind.Veo);

        var scenes = await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true));

        Assert.All(scenes, s => Assert.Equal(StorySceneStatus.Queued, s.Status));
        Assert.Equal(2, _queue.Items.Count);
        Assert.Equal(new[] { "charge", "charge" }, _metering.Log);
        Assert.Equal(2, (await _db.StoryGenerationJobs.IgnoreQueryFilters().ToListAsync()).Select(j => j.IdempotencyKey).Distinct().Count());
        Assert.Equal(StoryProjectStatus.Generating, (await _db.StoryProjects.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_project_budget_stops_a_run_that_would_exceed_it()
    {
        var project = AddProject(SceneVideoProviderKind.Veo, budget: 1.00m);

        await Assert.ThrowsAsync<ConflictException>(() => _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true)));
        Assert.Empty(_metering.Log);
    }

    [Fact]
    public async Task The_runner_submits_once_remembers_the_provider_id_then_polls_without_resubmitting()
    {
        var project = AddProject(SceneVideoProviderKind.Veo, scenes: 1);
        await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true));
        var jobId = _queue.Items[0].Job;

        _paid.Polls.Enqueue(new SceneClipResult(SceneClipState.Running, "op-1", null, null, null));
        await _service.RunAsync(jobId);
        Assert.Equal("op-1", (await _db.StoryGenerationJobs.IgnoreQueryFilters().SingleAsync()).ProviderJobId);
        Assert.Equal(1, _paid.Submits);

        // The worker "restarts": the next step polls from the saved id. A second submission would be a second bill.
        await _service.RunAsync(jobId);
        Assert.Equal(1, _paid.Submits);
        Assert.Equal(3, _queue.Items.Count);
        Assert.Equal(StoryClipService.PollEvery, _queue.Items[1].Delay);
        Assert.Equal(StoryClipService.PollEvery, _queue.Items[2].Delay);
    }

    [Fact]
    public async Task A_provider_failure_marks_the_scene_failed_refunds_and_ends_the_project_as_failed()
    {
        var project = AddProject(SceneVideoProviderKind.Veo, scenes: 1);
        await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true));
        _paid.SubmitResult = new SceneClipResult(SceneClipState.Failed, null, null, null, "Quota exceeded");

        await _service.RunAsync(_queue.Items[0].Job);

        var scene = (await _service.ListScenesAsync(_tenant, project.Id)).Single();
        Assert.Equal(StorySceneStatus.Failed, scene.Status);
        Assert.Equal("Quota exceeded", scene.Error);
        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
        Assert.Equal(StoryProjectStatus.Failed, (await _db.StoryProjects.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_provider_that_never_finishes_is_given_up_on_and_refunded()
    {
        var project = AddProject(SceneVideoProviderKind.Veo, scenes: 1);
        await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(true));
        await _service.RunAsync(_queue.Items[0].Job);

        _clock.UtcNow = _clock.UtcNow.Add(StoryClipService.GiveUpAfter).AddMinutes(1);
        _paid.Polls.Enqueue(new SceneClipResult(SceneClipState.Running, "op-1", null, null, null));
        await _service.RunAsync(_queue.Items[0].Job);

        Assert.Equal(StorySceneStatus.Failed, (await _service.ListScenesAsync(_tenant, project.Id)).Single().Status);
        Assert.Contains("refund", _metering.Log);
    }

    [Fact]
    public async Task The_simulator_makes_a_real_clip_stores_it_and_moves_the_project_to_review()
    {
        if (!await HaveFfmpegAsync()) return;
        var project = AddProject(SceneVideoProviderKind.Simulator, scenes: 2);

        await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false));
        foreach (var item in _queue.Items.ToList()) await _service.RunAsync(item.Job);

        var scenes = await _service.ListScenesAsync(_tenant, project.Id);
        Assert.All(scenes, s => { Assert.Equal(StorySceneStatus.Completed, s.Status); Assert.InRange(s.ActualMilliseconds!.Value, 7000, 9000); });
        Assert.Equal(2, _store.Saved.Count);
        Assert.Empty(_metering.Log);
        Assert.Equal(StoryProjectStatus.InReview, (await _db.StoryProjects.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(2, await _db.StoryMediaAssets.IgnoreQueryFilters().CountAsync(a => a.Kind == StoryMediaKind.SceneClip));
    }

    [Fact]
    public async Task Flow_parks_scenes_for_import_and_an_uploaded_clip_completes_one_scene()
    {
        if (!await HaveFfmpegAsync()) return;
        var project = AddProject(SceneVideoProviderKind.FlowImport, scenes: 2);

        var started = await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false));
        Assert.All(started, s => Assert.Equal(StorySceneStatus.AwaitingImport, s.Status));
        Assert.Empty(_queue.Items);

        await using var clip = File.OpenRead(await MakeClipAsync(8));
        var done = await _service.UploadClipAsync(_tenant, null, started[0].Id, clip, "scene1.mp4");

        Assert.Equal(StorySceneStatus.Completed, done.Status);
        var after = await _service.ListScenesAsync(_tenant, project.Id);
        Assert.Equal(StorySceneStatus.AwaitingImport, after[1].Status);
        Assert.Equal(StoryProjectStatus.Generating, (await _db.StoryProjects.IgnoreQueryFilters().SingleAsync()).Status);

        await using var second = File.OpenRead(await MakeClipAsync(8));
        await _service.UploadClipAsync(_tenant, null, started[1].Id, second, "scene2.mp4");
        Assert.Equal(StoryProjectStatus.InReview, (await _db.StoryProjects.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task An_upload_that_is_not_a_vertical_video_is_refused_and_nothing_is_stored()
    {
        if (!await HaveFfmpegAsync()) return;
        var project = AddProject(SceneVideoProviderKind.FlowImport, scenes: 1);
        var scenes = await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UploadClipAsync(_tenant, null, scenes[0].Id, new MemoryStream(new byte[] { 1, 2, 3, 4 }), "x.mp4"));
        await using var wide = File.OpenRead(await MakeClipAsync(4, vertical: false));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UploadClipAsync(_tenant, null, scenes[0].Id, wide, "wide.mp4"));
        Assert.Contains("landscape", ex.Message);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task Flagging_a_finished_clip_lets_only_that_scene_be_redone_and_keeps_the_original_file()
    {
        if (!await HaveFfmpegAsync()) return;
        var project = AddProject(SceneVideoProviderKind.Simulator, scenes: 2);
        await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false));
        foreach (var item in _queue.Items.ToList()) await _service.RunAsync(item.Job);
        var scenes = await _service.ListScenesAsync(_tenant, project.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.FlagSceneAsync(_tenant, scenes[0].Id, " "));
        var flagged = await _service.FlagSceneAsync(_tenant, scenes[0].Id, "Lips do not match the words");
        Assert.Equal(StorySceneStatus.Flagged, flagged.Status);
        Assert.Equal(StoryProjectStatus.ScriptApproved, (await _db.StoryProjects.IgnoreQueryFilters().SingleAsync()).Status);

        var before = _store.Saved.Count;
        await _service.RegenerateSceneAsync(_tenant, null, scenes[0].Id, new GenerateClipsRequest(false));
        foreach (var item in _queue.Items.Skip(2).ToList()) await _service.RunAsync(item.Job);

        Assert.Equal(before + 1, _store.Saved.Count);
        Assert.Equal(StorySceneStatus.Completed, (await _service.ListScenesAsync(_tenant, project.Id))[0].Status);
        Assert.Equal(2, await _db.StoryMediaAssets.IgnoreQueryFilters().CountAsync(a => a.StorageKey.Contains("scene-1-")) );
        Assert.Equal(3, _store.Saved.Count);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_upload_to_the_scene()
    {
        var project = AddProject(SceneVideoProviderKind.FlowImport, scenes: 1);
        var scenes = await _service.GenerateClipsAsync(_tenant, null, project.Id, new GenerateClipsRequest(false));
        var other = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(() => _service.ListScenesAsync(other, project.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.FlagSceneAsync(other, scenes[0].Id, "x"));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.UploadClipAsync(other, null, scenes[0].Id, new MemoryStream(new byte[] { 1 }), "x.mp4"));
    }
}
