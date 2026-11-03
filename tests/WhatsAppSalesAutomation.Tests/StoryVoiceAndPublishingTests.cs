using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

/// <summary>Voice-over on a scene (real ffmpeg when installed), the publish pack and publication record, the host diagnostics, and the studio ledger kinds.</summary>
public sealed class StoryVoiceAndPublishingTests : IDisposable
{
    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
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
            Log.Add("charge:" + request.Kind);
            return Task.FromResult(new StudioChargeResult(true, Guid.NewGuid(), false, null));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) { Log.Add("complete"); return Task.CompletedTask; }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default) { Log.Add("refund"); return Task.CompletedTask; }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class MemoryStore : ITenantDocumentStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public bool IsConfigured => true;

        public async Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Files[$"{tenantId}/{relativePath}"] = copy.ToArray();
            return $"{tenantId}/{relativePath}";
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Files[storageKey]));

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    private sealed class PaidSpeech : ISpeechProvider
    {
        private readonly IFfmpegService _ffmpeg;
        public PaidSpeech(IFfmpegService ffmpeg) => _ffmpeg = ffmpeg;
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public int MillisecondsPerLine { get; set; } = 1500;
        public string Name => "Fake";
        public decimal? EstimateCostUsd() => 0.02m;

        public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) return new SpeechResult(false, null, null, "Speech service down");
            var path = Path.Combine(Path.GetTempPath(), $"fake-speech-{Guid.NewGuid():N}.mp3");
            await _ffmpeg.CreateToneAsync(path, MillisecondsPerLine, cancellationToken);
            return new SpeechResult(true, path, 0.02m, null);
        }
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
    private readonly MemoryStore _store = new();
    private readonly FfmpegService _ffmpeg = new(new Snapshot());
    private readonly PaidSpeech _speech;
    private readonly StoryVoiceService _voice;
    private readonly StoryPublishingService _publishing;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly ITenantConfigOverrideProvider _config;

    public StoryVoiceAndPublishingTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new NoUser());
        _db.Database.EnsureCreated();
        _config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        _speech = new PaidSpeech(_ffmpeg);
        var clips = new StoryClipService(_db, _config, new SceneVideoProviderFactory(new ISceneVideoProvider[] { new FlowImportSceneVideoProvider() }), _metering, _store, _ffmpeg,
            Fake.Of<IStoryClipQueue>((m, a) => null), new Clock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<StoryClipService>.Instance);
        _voice = new StoryVoiceService(_db, _config, _speech, _ffmpeg, _store, _metering, clips);
        _publishing = new StoryPublishingService(_db, _config, new Clock());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CinematicScript Script() => new(
        "Guru Ji ka nyay", "Ek jhagda, ek hansi.", "hook", "summary", "Hinglish", "end",
        new[] { new CinematicCharacter("Guru Ji", "sage", "calm"), new CinematicCharacter("Sunita", "wife", "sharp") },
        new[] { new CinematicScene(1, 8, "s", "c", "t", "f", "k", "p", new[] { new CinematicDialogue("Guru Ji", "Shaant", "x", "Shaant", 500, 3000) }) });

    private async Task<(StoryProject Project, StoryScene Scene)> AddSceneWithClipAsync(StoryProjectStatus status = StoryProjectStatus.InReview, int lines = 2)
    {
        var script = new StoryScriptVersion { TenantId = _tenant, Number = 1, Json = JsonSerializer.Serialize(Script()), ContentHash = "h", Source = "Simulator", Status = StoryScriptStatus.Approved };
        var project = new StoryProject { TenantId = _tenant, Title = "Guru Ji", Topic = "t", Language = "Hinglish", Status = status, CurrentScriptVersionId = script.Id, TargetSeconds = 8 };
        script.ProjectId = project.Id;
        _db.StoryProjects.Add(project);
        _db.StoryScriptVersions.Add(script);

        var path = Path.Combine(Path.GetTempPath(), $"voice-test-{Guid.NewGuid():N}.mp4");
        await _ffmpeg.CreateTestClipAsync(path, 8, "t");
        string key;
        await using (var file = File.OpenRead(path)) key = await _store.SaveAsync(_tenant, "clips/1.mp4", file, "video/mp4");
        File.Delete(path);

        var asset = new StoryMediaAsset { TenantId = _tenant, ProjectId = project.Id, Kind = StoryMediaKind.SceneClip, StorageKey = key, ContentType = "video/mp4", DurationMs = 8000 };
        _db.StoryMediaAssets.Add(asset);
        var scene = new StoryScene
        {
            TenantId = _tenant, ProjectId = project.Id, ScriptVersionId = script.Id, Index = 1, TargetSeconds = 8, VideoPrompt = "p", Status = StorySceneStatus.Completed,
            ClipAssetId = asset.Id, ActualMilliseconds = 8000
        };
        _db.StoryScenes.Add(scene);
        for (var i = 1; i <= lines; i++)
            _db.StoryDialogueLines.Add(new StoryDialogueLine { TenantId = _tenant, SceneId = scene.Id, Order = i, SpeakerName = i % 2 == 1 ? "Guru Ji" : "Sunita", Text = $"Line {i}", StartMs = i * 3000 - 2500, EndMs = i * 3000 });
        await _db.SaveChangesAsync();
        return (project, scene);
    }

    [Fact]
    public async Task A_paid_voice_over_needs_the_cost_confirmed_and_does_nothing_until_it_is()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var (_, scene) = await AddSceneWithClipAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _voice.ApplyVoiceOverAsync(_tenant, null, scene.Id, new VoiceOverRequest(false)));

        Assert.Contains("0.04", ex.Message);
        Assert.Equal(0, _speech.Calls);
        Assert.Empty(_metering.Log);
    }

    [Fact]
    public async Task A_voice_over_replaces_the_audio_keeps_the_picture_and_keeps_the_original_clip()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var (project, scene) = await AddSceneWithClipAsync();
        var originalAsset = scene.ClipAssetId;

        var result = await _voice.ApplyVoiceOverAsync(_tenant, null, scene.Id, new VoiceOverRequest(true));

        Assert.Equal(2, _speech.Calls);
        Assert.Equal(0.04m, result.CostUsd);
        Assert.Equal(new[] { "charge:StoryVoice", "complete" }, _metering.Log);
        var fresh = await _db.StoryScenes.IgnoreQueryFilters().SingleAsync();
        Assert.True(fresh.VoiceOverApplied);
        Assert.NotEqual(originalAsset, fresh.ClipAssetId);
        Assert.True((await _db.StoryMediaAssets.IgnoreQueryFilters().SingleAsync(a => a.Id == originalAsset)).IsOriginal);

        var path = Path.Combine(Path.GetTempPath(), $"voice-check-{Guid.NewGuid():N}.mp4");
        try
        {
            var asset = await _db.StoryMediaAssets.IgnoreQueryFilters().SingleAsync(a => a.Id == fresh.ClipAssetId);
            await File.WriteAllBytesAsync(path, _store.Files[asset.StorageKey]);
            var probe = await _ffmpeg.ProbeAsync(path);
            Assert.True(probe.HasAudio);
            Assert.Equal((720, 1280), (probe.Width, probe.Height));
            Assert.InRange(probe.DurationSeconds, 7, 9);
        }
        finally { File.Delete(path); }

        Assert.Equal(1, await _db.StoryGenerationJobs.IgnoreQueryFilters().CountAsync(j => j.Kind == StoryJobKind.Speech && j.Status == StoryJobStatus.Completed));
        Assert.Equal(project.Id, (await _db.StoryGenerationJobs.IgnoreQueryFilters().SingleAsync()).ProjectId);
    }

    [Fact]
    public async Task A_line_that_takes_longer_than_its_slot_is_reported_not_squeezed()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var (_, scene) = await AddSceneWithClipAsync(lines: 1);
        _speech.MillisecondsPerLine = 5000;

        var result = await _voice.ApplyVoiceOverAsync(_tenant, null, scene.Id, new VoiceOverRequest(true));

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Guru Ji", warning);
        Assert.Contains("5.0s", warning);
    }

    [Fact]
    public async Task A_failing_speech_service_refunds_leaves_the_clip_alone_and_records_the_failure()
    {
        if (!await _ffmpeg.IsAvailableAsync()) return;
        var (_, scene) = await AddSceneWithClipAsync();
        var before = scene.ClipAssetId;
        _speech.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _voice.ApplyVoiceOverAsync(_tenant, null, scene.Id, new VoiceOverRequest(true)));

        Assert.Equal(new[] { "charge:StoryVoice", "refund" }, _metering.Log);
        var fresh = await _db.StoryScenes.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(before, fresh.ClipAssetId);
        Assert.False(fresh.VoiceOverApplied);
        Assert.Equal(StoryJobStatus.Failed, (await _db.StoryGenerationJobs.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Voice_over_is_refused_without_a_clip_after_approval_and_for_another_tenant()
    {
        var (project, scene) = await AddSceneWithClipAsync(StoryProjectStatus.Approved);

        await Assert.ThrowsAsync<ConflictException>(() => _voice.ApplyVoiceOverAsync(_tenant, null, scene.Id, new VoiceOverRequest(true)));
        project.Status = StoryProjectStatus.InReview;
        scene.Status = StorySceneStatus.Pending;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _voice.ApplyVoiceOverAsync(_tenant, null, scene.Id, new VoiceOverRequest(true)));
        await Assert.ThrowsAsync<NotFoundException>(() => _voice.ApplyVoiceOverAsync(Guid.NewGuid(), null, scene.Id, new VoiceOverRequest(true)));
    }

    [Fact]
    public void The_same_character_always_gets_the_same_voice_and_a_wife_gets_a_female_one()
    {
        Assert.Equal(OpenAiSpeechProvider.VoiceFor("Sunita", "sharp wife"), OpenAiSpeechProvider.VoiceFor("Sunita", "sharp wife"));
        Assert.Contains(OpenAiSpeechProvider.VoiceFor("Sunita", "sharp wife"), new[] { "coral", "nova", "shimmer" });
        Assert.Contains(OpenAiSpeechProvider.VoiceFor("Ramesh", "angry husband"), new[] { "onyx", "echo", "ash" });
    }

    // ----- publishing ------------------------------------------------------------------------------------------------------------------------

    private async Task<StoryProject> ApprovedProjectAsync()
    {
        var (project, _) = await AddSceneWithClipAsync(StoryProjectStatus.Approved);
        _db.StoryRenders.Add(new StoryRender { TenantId = _tenant, ProjectId = project.Id, Status = StoryJobStatus.Completed, ContentHash = "h" });
        await _db.SaveChangesAsync();
        return project;
    }

    [Fact]
    public async Task The_publish_pack_has_a_ready_to_paste_set_for_each_platform_and_only_for_an_approved_video()
    {
        var (draft, _) = await AddSceneWithClipAsync(StoryProjectStatus.InReview);
        await Assert.ThrowsAsync<ConflictException>(() => _publishing.GetPackAsync(_tenant, draft.Id));

        var project = await ApprovedProjectAsync();
        var pack = await _publishing.GetPackAsync(_tenant, project.Id);

        Assert.Equal(3, pack.Platforms.Count);
        var youtube = pack.Platforms.Single(p => p.Platform == StoryPlatform.YouTubeShorts);
        Assert.EndsWith("#Shorts", youtube.Title);
        Assert.InRange(youtube.Title.Length, 1, 100);
        Assert.Contains("#GuruJi", youtube.Hashtags);
        Assert.All(pack.Platforms, p => Assert.NotEmpty(p.Rules));
        await Assert.ThrowsAsync<NotFoundException>(() => _publishing.GetPackAsync(Guid.NewGuid(), project.Id));
    }

    [Fact]
    public async Task A_publication_is_recorded_with_its_address_and_shows_in_the_pack()
    {
        var project = await ApprovedProjectAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _publishing.RecordAsync(_tenant, null, project.Id, new RecordPublicationRequest(StoryPlatform.YouTubeShorts, "not a link")));
        await Assert.ThrowsAsync<ArgumentException>(() => _publishing.RecordAsync(_tenant, null, project.Id, new RecordPublicationRequest(StoryPlatform.YouTubeShorts, "javascript:alert(1)")));
        var saved = await _publishing.RecordAsync(_tenant, null, project.Id, new RecordPublicationRequest(StoryPlatform.InstagramReels, "https://www.instagram.com/reel/abc/"));

        Assert.Equal(StoryPlatform.InstagramReels, saved.Platform);
        var pack = await _publishing.GetPackAsync(_tenant, project.Id);
        Assert.Equal("https://www.instagram.com/reel/abc/", Assert.Single(pack.Published).Url);
    }

    // ----- diagnostics and ledger -------------------------------------------------------------------------------------------------------------

    private sealed class Options : IOptionsSnapshot<CinematicOptions>
    {
        public CinematicOptions Value { get; init; } = new();
        public CinematicOptions Get(string? name) => Value;
    }

    private sealed class Keys : WhatsAppSalesAutomation.Application.Common.Interfaces.IPlatformAiConfigProvider
    {
        public string? Google { get; init; }
        public string? OpenAi { get; init; }
        public bool GoogleOn { get; init; } = true;

        public WhatsAppSalesAutomation.Application.Common.Interfaces.AiCredentials Get() =>
            new("Simulated", "Simulated", null, "m", "v", "u", OpenAi, "m", "e", "u", Google, "m", "e", "u", GoogleEnabled: GoogleOn);
    }

    [Fact]
    public async Task Diagnostics_name_what_is_missing_without_calling_any_paid_service()
    {
        var bare = new StoryDiagnosticsService(_ffmpeg, new Options(), _store, new Keys());
        var checks = (await bare.RunAsync()).ToDictionary(c => c.Key);

        Assert.True(checks["veo"].Ok);        // Veo is switched off by default, so no key is needed and the line says so
        Assert.Contains("switched off", checks["veo"].Detail);
        Assert.True(checks["pictures"].Ok);    // placeholders are chosen, so no key is needed
        Assert.True(checks["storage"].Ok);

        var chosen = new Options { Value = new CinematicOptions { VideoProvider = "Veo", CharacterImageProvider = "OpenAI", SpeechProvider = "OpenAI" } };
        var veoNoKey = (await new StoryDiagnosticsService(_ffmpeg, chosen, _store, new Keys()).RunAsync()).ToDictionary(c => c.Key);
        Assert.False(veoNoKey["veo"].Ok);      // switched on but no Gemini key under AI Providers
        var after = (await new StoryDiagnosticsService(_ffmpeg, chosen, _store, new Keys { Google = "g" }).RunAsync()).ToDictionary(c => c.Key);
        Assert.True(after["veo"].Ok);          // the Google key from AI Providers
        Assert.Contains("AI Providers", after["pictures"].Detail);
        Assert.False(after["pictures"].Ok);    // OpenAI chosen but no ChatGPT key
        Assert.False(after["voice"].Ok);

        var withKeys = (await new StoryDiagnosticsService(_ffmpeg, chosen, _store, new Keys { Google = "g", OpenAi = "o" }).RunAsync()).ToDictionary(c => c.Key);
        Assert.True(withKeys["pictures"].Ok);
        Assert.True(withKeys["voice"].Ok);

        // Switching Gemini off under AI Providers takes Veo away even though a key is saved.
        var off = (await new StoryDiagnosticsService(_ffmpeg, chosen, _store, new Keys { Google = "g", GoogleOn = false }).RunAsync()).ToDictionary(c => c.Key);
        Assert.False(off["veo"].Ok);
    }

    [Theory]
    [InlineData(StudioActivityKind.StoryScript, QuotaType.ContentGenerations)]
    [InlineData(StudioActivityKind.StoryCharacterImage, QuotaType.ContentGenerations)]
    [InlineData(StudioActivityKind.StorySceneClip, QuotaType.VideoRenders)]
    [InlineData(StudioActivityKind.StoryVoice, QuotaType.ContentGenerations)]
    public void Each_kind_of_story_work_has_its_own_ledger_category_on_the_right_quota(StudioActivityKind kind, QuotaType quota)
    {
        var rule = StudioChargeCatalog.For(kind);

        Assert.Equal(quota, rule.QuotaType);
        Assert.StartsWith("Story", rule.Label);
    }
}
