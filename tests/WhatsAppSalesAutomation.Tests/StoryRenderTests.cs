using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Subtitle timing (pure) and the final render (with the real ffmpeg when it is installed).</summary>
public sealed class StoryRenderTests : IDisposable
{
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

    private sealed class MemoryStore : ITenantDocumentStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public bool IsConfigured => true;

        public async Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            var key = $"{tenantId}/{relativePath}";
            Files[key] = copy.ToArray();
            return key;
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Files[storageKey]));

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private sealed class Queue : IStoryRenderQueue
    {
        public List<Guid> Items { get; } = new();

        public void Enqueue(Guid renderId, Guid tenantId) => Items.Add(renderId);
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
    private readonly MemoryStore _store = new();
    private readonly Queue _queue = new();
    private readonly FfmpegService _ffmpeg = new(new Snapshot());
    private readonly StoryRenderService _service;
    private readonly Guid _tenant = Guid.NewGuid();

    public StoryRenderTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();
        var config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        _service = new StoryRenderService(_db, config, _store, _ffmpeg, _queue, NullLogger<StoryRenderService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static StoryDialogueLine Line(int order, string text, int start, int end) =>
        new() { Order = order, Text = text, StartMs = start, EndMs = end, SpeakerName = "x" };

    [Fact]
    public void Subtitle_cues_are_shifted_by_the_real_length_of_the_scenes_before_them()
    {
        var cues = SubtitleBuilder.Cues(new (int, int, IReadOnlyList<StoryDialogueLine>)[]
        {
            (0, 7600, new[] { Line(1, "Pehli line", 500, 3000) }),
            (7600, 8000, new[] { Line(1, "Doosri line", 500, 3000), Line(2, "Teesri line", 3500, 9000) })
        });

        Assert.Equal(new[] { 500, 8100, 11100 }, cues.Select(c => c.StartMs));
        Assert.Equal(15600, cues[2].EndMs);   // a line that runs past its scene is cut at the scene end, not into the next scene
    }

    [Fact]
    public void An_srt_has_numbered_cues_with_millisecond_timestamps_and_keeps_devanagari()
    {
        var srt = SubtitleBuilder.Srt(new[] { new SubtitleBuilder.Cue(1500, 63250, "शांत हो जाओ") });

        Assert.Equal("1\n00:00:01,500 --> 00:01:03,250\nशांत हो जाओ\n\n", srt);
    }

    private async Task<Guid> ProjectWithClipsAsync(int scenes, StoryProjectStatus status = StoryProjectStatus.InReview)
    {
        var script = new StoryScriptVersion { TenantId = _tenant, Number = 1, Json = "{}", ContentHash = "hash-1", Source = "Simulator", Status = StoryScriptStatus.Approved };
        var project = new StoryProject
        {
            TenantId = _tenant, Title = "t", Topic = "t", Language = "Hinglish", Status = status, TargetSeconds = 30, CurrentScriptVersionId = script.Id
        };
        script.ProjectId = project.Id;
        _db.StoryProjects.Add(project);
        _db.StoryScriptVersions.Add(script);

        for (var i = 1; i <= scenes; i++)
        {
            var path = Path.Combine(Path.GetTempPath(), $"render-test-{Guid.NewGuid():N}.mp4");
            await _ffmpeg.CreateTestClipAsync(path, 8, "t");
            string key;
            await using (var clipFile = File.OpenRead(path)) key = await _store.SaveAsync(_tenant, $"clips/{i}.mp4", clipFile, "video/mp4");
            File.Delete(path);

            var asset = new StoryMediaAsset { TenantId = _tenant, ProjectId = project.Id, Kind = StoryMediaKind.SceneClip, StorageKey = key, ContentType = "video/mp4" };
            _db.StoryMediaAssets.Add(asset);
            var scene = new StoryScene
            {
                TenantId = _tenant, ProjectId = project.Id, ScriptVersionId = script.Id, Index = i, TargetSeconds = 8, ActualMilliseconds = 8000,
                VideoPrompt = "p", Status = StorySceneStatus.Completed, ClipAssetId = asset.Id
            };
            _db.StoryScenes.Add(scene);
            _db.StoryDialogueLines.Add(new StoryDialogueLine { TenantId = _tenant, SceneId = scene.Id, Order = 1, SpeakerName = "Guru Ji", Text = $"शांत हो जाओ {i}", StartMs = 500, EndMs = 3500 });
        }

        await _db.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task A_render_needs_every_scene_finished()
    {
        var project = new StoryProject { TenantId = _tenant, Title = "t", Topic = "t", Language = "Hinglish", Status = StoryProjectStatus.Generating };
        _db.StoryProjects.Add(project);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => _service.StartRenderAsync(_tenant, null, project.Id));
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task The_four_scene_story_is_merged_with_subtitles_into_a_valid_vertical_mp4_and_stored()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var projectId = await ProjectWithClipsAsync(4);

        var started = await _service.StartRenderAsync(_tenant, null, projectId);
        Assert.Equal(StoryJobStatus.Queued, started.Status);
        await Assert.ThrowsAsync<ConflictException>(() => _service.StartRenderAsync(_tenant, null, projectId));   // one at a time

        await _service.RunAsync(_queue.Items.Single());

        var render = (await _service.ListRendersAsync(_tenant, projectId)).Single();
        Assert.True(render.Status == StoryJobStatus.Completed, render.Error);
        Assert.InRange(render.DurationSeconds!.Value, 31, 33);
        Assert.True(render.HasVideo);

        // What was stored is a real file that ffprobe agrees with.
        var final = Path.Combine(Path.GetTempPath(), $"final-check-{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var stream = (await _service.OpenFinalAsync(_tenant, render.Id))!)
            await using (var file = File.Create(final)) await stream.CopyToAsync(file);
            var probe = await _ffmpeg.ProbeAsync(final);
            Assert.Equal((1080, 1920), (probe.Width, probe.Height));
            Assert.True(probe.HasAudio);
        }
        finally
        {
            File.Delete(final);
        }

        Assert.Equal("hash-1", (await _db.StoryRenders.IgnoreQueryFilters().SingleAsync()).ContentHash);
    }

    [Fact]
    public async Task A_missing_clip_fails_the_render_with_a_sentence_and_stores_nothing()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var projectId = await ProjectWithClipsAsync(2);
        _store.Files.Remove(_store.Files.Keys.First(k => k.EndsWith("clips/1.mp4")));
        var before = _store.Files.Count;

        await _service.StartRenderAsync(_tenant, null, projectId);
        await _service.RunAsync(_queue.Items.Single());

        var render = (await _service.ListRendersAsync(_tenant, projectId)).Single();
        Assert.Equal(StoryJobStatus.Failed, render.Status);
        Assert.False(string.IsNullOrWhiteSpace(render.Error));
        Assert.Equal(before, _store.Files.Count);
    }

    [Fact]
    public async Task Another_tenant_cannot_download_the_video_or_a_clip()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var projectId = await ProjectWithClipsAsync(1);
        await _service.StartRenderAsync(_tenant, null, projectId);
        await _service.RunAsync(_queue.Items.Single());
        var render = (await _service.ListRendersAsync(_tenant, projectId)).Single();
        var scene = await _db.StoryScenes.IgnoreQueryFilters().SingleAsync();
        var other = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(() => _service.OpenFinalAsync(other, render.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.OpenClipAsync(other, scene.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ListRendersAsync(other, projectId));
    }
}
