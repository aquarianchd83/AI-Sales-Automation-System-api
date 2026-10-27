using System.Text.Json;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Making a video: what may be started, what it costs and when that is returned, the steps of the background work (voice, plan, collect), and
/// every way a render ends - done, failed, cancelled, timed out - leaving no voice files behind and no charge that should have been returned.
/// The voice and video services are scripted fakes: these are code-level tests, not live checks of Google or Shotstack.
/// </summary>
public sealed class VideoRenderTests : IDisposable
{
    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class Admin : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Email => "owner@clinic.example";
        public IReadOnlyList<string> Roles => new[] { "Admin" };
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
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
        public DateTime Now { get; set; } = new(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    private sealed class MemoryStore : ITenantDocumentStore
    {
        public bool IsConfigured { get; set; } = true;
        public Dictionary<string, byte[]> Files { get; } = new();

        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var key = "s3:" + FolderOf(tenantId) + relativePath;
            Files[key] = copy.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Files[storageKey]));

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            Files.Remove(storageKey);
            return Task.CompletedTask;
        }

        public string FolderOf(Guid tenantId) => $"private/{tenantId:N}/product-knowledge/";
    }

    private sealed class Links : ITenantFileLinks
    {
        public List<string> Made { get; } = new();

        public string CreateTemporaryReadUrl(Guid tenantId, string storageKey, TimeSpan lifetime)
        {
            Made.Add(storageKey);
            return "https://signed.example/" + Uri.EscapeDataString(storageKey);
        }
    }

    private sealed class Queue : IVideoRenderQueue
    {
        public List<Guid> Started { get; } = new();
        public List<(Guid Id, TimeSpan Delay)> Polls { get; } = new();
        public void EnqueueStart(Guid jobId, Guid tenantId) => Started.Add(jobId);
        public void SchedulePoll(Guid jobId, Guid tenantId, TimeSpan delay) => Polls.Add((jobId, delay));
    }

    private sealed class Voice : IVoiceSynthesizer
    {
        public bool IsConfigured => true;
        public List<(string Text, string Language, string Gender)> Spoken { get; } = new();
        public double Seconds { get; set; } = 3;
        public Exception? Fail { get; set; }
        public int FailOnCall { get; set; } = 1;
        public Action? After { get; set; }

        public Task<VoiceClip> SynthesizeAsync(string text, string languageCode, string gender, CancellationToken cancellationToken = default)
        {
            Spoken.Add((text, languageCode, gender));
            if (Fail is not null && Spoken.Count >= FailOnCall)
                throw Fail;
            var clip = new VoiceClip(new byte[] { 1, 2, 3, (byte)Spoken.Count }, Seconds);
            After?.Invoke();
            return Task.FromResult(clip);
        }
    }

    private sealed class Renderer : IVideoRenderer
    {
        public string Name => "Shotstack";
        public bool IsConfigured => true;
        public RenderPlan? Submitted { get; private set; }
        public Queue<object> Statuses { get; } = new();
        public Exception? FailSubmit { get; set; }
        public Exception? FailDownload { get; set; }
        public byte[] Video { get; set; } = new byte[] { 9, 9, 9, 9, 9 };

        public Task<string> SubmitAsync(RenderPlan plan, CancellationToken cancellationToken = default)
        {
            if (FailSubmit is not null)
                throw FailSubmit;
            Submitted = plan;
            return Task.FromResult("render-1");
        }

        public Task<RenderStatus> GetStatusAsync(string externalId, CancellationToken cancellationToken = default)
        {
            var next = Statuses.Count > 1 ? Statuses.Dequeue() : Statuses.Peek();
            if (next is Exception ex)
                throw ex;
            return Task.FromResult((RenderStatus)next);
        }

        public async Task DownloadAsync(string url, long maxBytes, Stream destination, CancellationToken cancellationToken = default)
        {
            if (FailDownload is not null)
                throw FailDownload;
            await destination.WriteAsync(Video, cancellationToken);
        }
    }

    private sealed class FakeMetering : IStudioMeteringService
    {
        public bool DenySecond { get; set; }
        public bool DenyAll { get; set; }
        public List<StudioChargeRequest> Charges { get; } = new();
        public List<Guid> Completed { get; } = new();
        public List<Guid> Refunded { get; } = new();

        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
        {
            if (DenyAll || (DenySecond && Charges.Count == 1))
                return Task.FromResult(new StudioChargeResult(false, Guid.Empty, false, "Your video renders are used up for this period."));
            Charges.Add(request);
            return Task.FromResult(new StudioChargeResult(true, Guid.NewGuid(), false, null));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default)
        {
            Completed.Add(activityId);
            return Task.CompletedTask;
        }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default)
        {
            Refunded.Add(activityId);
            return Task.CompletedTask;
        }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Routing : IAiFeatureRouting
    {
        public bool Live { get; set; } = true;
        public AiFeatureChoice Resolve(string feature) => new(Live, "OpenAI");
    }

    private sealed class Snapshot : IOptionsSnapshot<RenderOptions>
    {
        public RenderOptions Value { get; set; } = new() { ShotstackApiKey = "s", GoogleTtsApiKey = "g", MaxConcurrent = 2 };
        public RenderOptions Get(string? name) => Value;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Clock _clock = new();
    private readonly MemoryStore _store = new();
    private readonly Links _links = new();
    private readonly Queue _queue = new();
    private readonly Voice _voice = new();
    private readonly Renderer _renderer = new();
    private readonly FakeMetering _metering = new();
    private readonly Routing _routing = new();
    private readonly Snapshot _options = new();
    private readonly Admin _admin = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public VideoRenderTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _tenant.TenantId = _a;
        _db = new SqliteApplicationDbContext(options, _tenant, new NoUser());
        _db.Database.EnsureCreated();
        var tick = 0;
        _db.BeforeSave = db =>
        {
            foreach (var entry in db.ChangeTracker.Entries<WhatsAppSalesAutomation.Domain.Common.BaseEntity>().Where(e => e.State == EntityState.Added))
                entry.Entity.CreatedAt = _clock.Now.AddSeconds(++tick);
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private VideoRenderService Service() => new(_db, _store, _queue, _metering, _routing, _options, _renderer, _voice, _admin, _clock);

    private VideoRenderProcessor Processor() => new(_db, _store, _links, _voice, _renderer, _queue, _metering, _clock, _options, NullLogger<VideoRenderProcessor>.Instance);

    /// <summary>A script with two scenes, each with a picture kept in the tenant's folder.</summary>
    private async Task<ContentVersion> Script(Guid? tenant = null, bool simulated = false, bool withPictures = true, string language = "Hindi")
    {
        var tenantId = tenant ?? _a;
        var project = new ContentProject { TenantId = tenantId, Title = "Cataract awareness", Goal = "Bookings", Language = language };
        var version = new ContentVersion { TenantId = tenantId, ProjectId = project.Id, Number = 3, PromptVersion = "v", Provider = "OpenAI", ContentHash = new string('c', 64), IsSimulated = simulated };
        _db.ContentProjects.Add(project);
        _db.ContentVersions.Add(version);
        for (var i = 1; i <= 2; i++)
        {
            var asset = new StudioAsset
            {
                TenantId = tenantId, Kind = StudioAssetKind.Upload, Name = $"Pic {i}", ContentType = "image/png", StorageKey = $"s3:private/{tenantId:N}/product-knowledge/studio/assets/202610/pic{i}.png",
                Checksum = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), LicenseName = "Provided by you", RightsConfirmed = true
            };
            _db.StudioAssets.Add(asset);
            _db.ContentScenes.Add(new ContentScene
            {
                TenantId = tenantId, VersionId = version.Id, Order = i, Narration = $"Scene {i} narration.", OnScreenText = i == 1 ? "Welcome" : null, DurationSeconds = 5,
                ChosenAssetId = withPictures ? asset.Id : null, ClaimStatus = i == 2 ? ClaimStatus.Unsupported : ClaimStatus.Supported
            });
        }

        await _db.SaveChangesAsync();
        return version;
    }

    private async Task<Guid> Started(ContentVersion version, string? aspect = null)
    {
        var dto = await Service().StartAsync(_a, version.Id, new StartRenderRequest(aspect, null));
        return dto.Id;
    }

    private RenderJob Job(Guid id) => _db.RenderJobs.AsNoTracking().Single(j => j.Id == id);

    // ----- starting --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_video_is_charged_twice_up_front_the_render_and_the_voice_and_queued_with_what_it_was_made_from()
    {
        var version = await Script();

        var job = await Service().StartAsync(_a, version.Id, new StartRenderRequest("9:16", "male"));

        Assert.Equal(("Queued", 3, "9:16", "Male", "Shotstack", false, true), (job.Status, job.VersionNumber, job.AspectRatio, job.VoiceGender, job.Renderer, job.IsSimulated, job.IsAiVoice));
        Assert.Equal(1, job.FlaggedScenes); // a preview is allowed with a flagged scene, and the record says so
        Assert.Equal(new[] { StudioActivityKind.VideoRender, StudioActivityKind.VoiceOver }, _metering.Charges.Select(c => c.Kind));
        Assert.Equal(job.Id, Assert.Single(_queue.Started));
        var stored = Job(job.Id);
        Assert.Equal(("hi-IN", new string('c', 64)), (stored.LanguageCode, stored.ContentHash));
        Assert.NotNull(stored.RenderActivityId);
        Assert.NotNull(stored.VoiceActivityId);
    }

    [Fact]
    public async Task A_video_is_refused_until_every_scene_has_a_picture_and_never_made_from_a_test_script()
    {
        var noPictures = await Script(withPictures: false);
        Assert.Contains("2 of 2 scenes have no picture", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(_a, noPictures.Id, new StartRenderRequest(null, null)))).Message);

        var test = await Script(simulated: true);
        Assert.Contains("test mode", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(_a, test.Id, new StartRenderRequest(null, null)))).Message);

        var good = await Script();
        await Assert.ThrowsAsync<ValidationException>(() => Service().StartAsync(_a, good.Id, new StartRenderRequest("4:3", null)));

        Assert.Empty(_metering.Charges);
        Assert.Empty(_queue.Started);
        Assert.Empty(await _db.RenderJobs.ToListAsync());
    }

    [Fact]
    public async Task A_video_is_refused_with_a_clear_reason_when_the_platform_is_not_set_up_for_it()
    {
        var version = await Script();

        _options.Value = new RenderOptions { ShotstackApiKey = "s" };
        Assert.Contains("video service key and a voice key", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(_a, version.Id, new StartRenderRequest(null, null)))).Message);

        _options.Value = new RenderOptions { ShotstackApiKey = "s", GoogleTtsApiKey = "g" };
        _store.IsConfigured = false;
        Assert.Contains("AWS S3", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(_a, version.Id, new StartRenderRequest(null, null)))).Message);

        Assert.Empty(_metering.Charges);
    }

    [Fact]
    public async Task A_tenant_makes_one_video_at_a_time_and_the_platform_only_so_many_at_once()
    {
        var version = await Script();
        var first = await Started(version);

        Assert.Contains("already being made", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(_a, version.Id, new StartRenderRequest(null, null)))).Message);

        // Another tenant is not held up by this one's video, until the platform limit (2) is reached.
        var theirs = await Script(_b);
        _tenant.TenantId = _b;
        await Service().StartAsync(_b, theirs.Id, new StartRenderRequest(null, null));
        _tenant.TenantId = _a;
        var c = Guid.NewGuid();
        var other = await Script(c);
        _tenant.TenantId = c;
        Assert.Contains("Many videos are being made", (await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(c, other.Id, new StartRenderRequest(null, null)))).Message);
        _tenant.TenantId = _a;

        // Once it ends the tenant can ask again.
        await Service().CancelAsync(_a, first);
        await Started(version);
    }

    [Fact]
    public async Task When_the_second_charge_is_denied_the_first_is_returned_and_nothing_is_queued()
    {
        var version = await Script();
        _metering.DenySecond = true;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().StartAsync(_a, version.Id, new StartRenderRequest(null, null)));

        Assert.Contains("used up", ex.Message);
        Assert.Single(_metering.Refunded);
        Assert.Empty(_queue.Started);
        Assert.Empty(await _db.RenderJobs.ToListAsync());
    }

    [Fact]
    public async Task In_the_platforms_test_mode_nothing_is_charged_spoken_or_rendered_and_no_video_is_pretended()
    {
        _routing.Live = false;
        var version = await Script();
        var id = await Started(version);

        await Processor().StartAsync(id);

        var job = Job(id);
        Assert.Equal((RenderJobStatus.Succeeded, true, false, 100, null), (job.Status, job.IsSimulated, job.IsAiVoice, job.Progress, job.OutputStorageKey));
        Assert.Contains("no video was made", job.Stage);
        Assert.Equal(10, job.OutputSeconds);
        Assert.Empty(_metering.Charges);
        Assert.Empty(_voice.Spoken);
        Assert.Null(_renderer.Submitted);
        Assert.Contains("test render", (await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenVideoAsync(_a, id))).Message);
    }

    // ----- the first step --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_step_speaks_every_scene_keeps_the_voice_privately_and_gives_the_renderer_a_plan_with_short_lived_links_timed_to_the_voice()
    {
        var version = await Script();
        _voice.Seconds = 7.3; // scene 1 needs longer than the 5 s the script planned; scene 2 as well
        var id = await Started(version, "1:1");

        await Processor().StartAsync(id);

        Assert.Equal(new[] { ("Scene 1 narration.", "hi-IN", "Female"), ("Scene 2 narration.", "hi-IN", "Female") }, _voice.Spoken);
        Assert.Equal(2, _store.Files.Keys.Count(k => k.Contains($"private/{_a:N}/product-knowledge/studio/audio/{id:N}/")));
        var plan = _renderer.Submitted!;
        Assert.Equal("1:1", plan.AspectRatio);
        Assert.Equal(new[] { 0.0, 7.7 }, plan.Scenes.Select(s => s.StartSeconds));
        Assert.Equal(new[] { 7.7, 7.7 }, plan.Scenes.Select(s => s.LengthSeconds));
        Assert.Equal(new[] { "Welcome", null }, plan.Scenes.Select(s => s.OnScreenText));
        Assert.All(plan.Scenes, s => Assert.StartsWith("https://signed.example/", s.ImageUrl));
        Assert.All(_links.Made, k => Assert.Contains($"private/{_a:N}/", k)); // links only for the tenant's own files
        var job = Job(id);
        Assert.Equal((RenderJobStatus.Rendering, "render-1", 45, 15.4), (job.Status, job.ExternalRenderId, job.Progress, job.OutputSeconds));
        Assert.Equal(PollEvery, Assert.Single(_queue.Polls).Delay);
        Assert.Empty(_metering.Completed); // nothing is final until the video exists
    }

    private static readonly TimeSpan PollEvery = RenderLimits.PollEvery;

    [Fact]
    public async Task A_repeated_message_does_the_work_once_and_a_finished_job_is_left_alone()
    {
        var version = await Script();
        var id = await Started(version);

        await Processor().StartAsync(id);
        await Processor().StartAsync(id);

        Assert.Equal(2, _voice.Spoken.Count);

        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Done, 100, "https://cdn.shotstack.io/v.mp4", null));
        await Processor().PollAsync(id);
        var spoken = _voice.Spoken.Count;
        await Processor().PollAsync(id);
        await Processor().StartAsync(id);
        Assert.Equal(spoken, _voice.Spoken.Count);
        Assert.Equal(RenderJobStatus.Succeeded, Job(id).Status);
    }

    [Fact]
    public async Task A_picture_that_is_not_in_the_tenants_folder_is_never_given_to_the_renderer()
    {
        var version = await Script();
        var asset = await _db.StudioAssets.FirstAsync();
        asset.StorageKey = $"s3:private/{_b:N}/product-knowledge/studio/assets/202610/theirs.png";
        await _db.SaveChangesAsync();
        var id = await Started(version);

        await Processor().StartAsync(id);

        Assert.Equal(RenderJobStatus.Failed, Job(id).Status);
        Assert.DoesNotContain(_links.Made, k => k.Contains(_b.ToString("N")));
        Assert.Null(_renderer.Submitted);
    }

    // ----- the second step -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task While_the_renderer_works_progress_moves_and_the_next_check_is_scheduled()
    {
        var version = await Script();
        var id = await Started(version);
        await Processor().StartAsync(id);
        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Working, 55, null, null));

        await Processor().PollAsync(id);

        var job = Job(id);
        Assert.Equal((RenderJobStatus.Rendering, 69), (job.Status, job.Progress));
        Assert.Equal(2, _queue.Polls.Count);
    }

    [Fact]
    public async Task A_finished_video_is_copied_into_the_tenants_folder_the_voice_files_are_deleted_and_both_charges_are_completed()
    {
        var version = await Script();
        var id = await Started(version);
        await Processor().StartAsync(id);
        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Done, 100, "https://cdn.shotstack.io/v.mp4", null));

        await Processor().PollAsync(id);

        var job = Job(id);
        Assert.Equal((RenderJobStatus.Succeeded, 100, 5L), (job.Status, job.Progress, job.OutputBytes));
        Assert.Contains($"private/{_a:N}/product-knowledge/studio/renders/{id:N}.mp4", job.OutputStorageKey);
        Assert.DoesNotContain(_store.Files.Keys, k => k.Contains("/studio/audio/"));
        Assert.Equal(2, _metering.Completed.Count);
        Assert.Empty(_metering.Refunded);

        var video = await Service().OpenVideoAsync(_a, id);
        Assert.Equal(("video/mp4", 5L), (video.ContentType, video.Length));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenVideoAsync(_b, id));
        Assert.True((await Service().GetAsync(_a, id)).HasVideo);
    }

    [Fact]
    public async Task A_voice_failure_ends_the_job_with_the_reason_removes_the_voice_files_and_returns_both_charges()
    {
        var version = await Script();
        _voice.Fail = new VideoProviderException("The voice service rejected the platform's key.");
        _voice.FailOnCall = 2; // the first scene is spoken and kept, the second fails
        var id = await Started(version);

        await Processor().StartAsync(id);

        var job = Job(id);
        Assert.Equal(RenderJobStatus.Failed, job.Status);
        Assert.Contains("rejected the platform's key", job.Error);
        Assert.Contains("Nothing was charged", job.Error);
        Assert.Empty(_store.Files.Keys.Where(k => k.Contains("/studio/audio/"))); // no orphan files
        Assert.Equal(2, _metering.Refunded.Count);
        Assert.Empty(_metering.Completed);
        Assert.Null(_renderer.Submitted);
    }

    [Fact]
    public async Task A_renderer_that_refuses_or_reports_failure_or_cannot_be_downloaded_from_ends_the_job_and_leaves_nothing()
    {
        // Refuses the plan.
        var version = await Script();
        _renderer.FailSubmit = new VideoProviderException("The video service could not use this script or its pictures.");
        var first = await Started(version);
        await Processor().StartAsync(first);
        Assert.Contains("could not use this script", Job(first).Error);
        Assert.Empty(_store.Files);

        // Reports failure while rendering.
        _renderer.FailSubmit = null;
        var second = await Started(version);
        await Processor().StartAsync(second);
        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Failed, 0, null, "Invalid image"));
        await Processor().PollAsync(second);
        Assert.Contains("Invalid image", Job(second).Error);
        Assert.Empty(_store.Files);

        // Finishes, but the file cannot be fetched.
        var third = await Started(version);
        await Processor().StartAsync(third);
        _renderer.Statuses.Clear();
        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Done, 100, "https://cdn.shotstack.io/v.mp4", null));
        _renderer.FailDownload = new HttpRequestException("down");
        await Processor().PollAsync(third);
        var job = Job(third);
        Assert.Equal((RenderJobStatus.Failed, null), (job.Status, job.OutputStorageKey));
        Assert.Contains("could not be reached", job.Error);
        Assert.Empty(_store.Files);
        Assert.Equal(6, _metering.Refunded.Count); // two charges for each of the three attempts
    }

    [Fact]
    public async Task A_renderer_that_stops_answering_is_given_a_few_tries_and_one_that_never_finishes_is_stopped()
    {
        var version = await Script();
        var id = await Started(version);
        await Processor().StartAsync(id);
        _renderer.Statuses.Enqueue(new HttpRequestException("down"));

        for (var i = 1; i < RenderLimits.MaxPollFailures; i++)
        {
            await Processor().PollAsync(id);
            Assert.Equal(RenderJobStatus.Rendering, Job(id).Status); // still hopeful
        }

        await Processor().PollAsync(id);
        Assert.Equal(RenderJobStatus.Failed, Job(id).Status);

        // And one that is simply slow.
        var again = await Started(version);
        await Processor().StartAsync(again);
        _renderer.Statuses.Clear();
        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Working, 50, null, null));
        _clock.Now = _clock.Now.Add(RenderLimits.MaxDuration).AddMinutes(1);
        await Processor().PollAsync(again);
        Assert.Contains("too long", Job(again).Stage);
        Assert.Equal(RenderJobStatus.Failed, Job(again).Status);
    }

    // ----- cancelling ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_video_cancelled_before_it_starts_is_cancelled_at_once_and_returns_both_charges()
    {
        var version = await Script();
        var id = await Started(version);

        var dto = await Service().CancelAsync(_a, id);

        Assert.Equal("Cancelled", dto.Status);
        Assert.Equal(2, _metering.Refunded.Count);
        await Processor().StartAsync(id); // the queued message arrives later and does nothing
        Assert.Empty(_voice.Spoken);
        await Assert.ThrowsAsync<ConflictException>(() => Service().CancelAsync(_a, id));
    }

    [Fact]
    public async Task A_video_cancelled_while_rendering_is_stopped_at_the_next_check_its_result_never_collected_and_its_charges_returned()
    {
        var version = await Script();
        var id = await Started(version);
        await Processor().StartAsync(id);

        var asked = await Service().CancelAsync(_a, id);
        Assert.Equal(("Rendering", "Cancelling…"), (asked.Status, asked.Stage));

        _renderer.Statuses.Enqueue(new RenderStatus(RenderPhase.Done, 100, "https://cdn.shotstack.io/v.mp4", null));
        await Processor().PollAsync(id);

        var job = Job(id);
        Assert.Equal((RenderJobStatus.Cancelled, null), (job.Status, job.OutputStorageKey));
        Assert.Equal(2, _metering.Refunded.Count);
        Assert.Empty(_store.Files);
    }

    [Fact]
    public async Task A_video_cancelled_while_the_voice_is_being_made_stops_before_the_next_scene_and_removes_what_was_made()
    {
        var version = await Script();
        var id = await Started(version);
        // The tenant presses Cancel just after the first scene has been spoken.
        _voice.After = () => _db.RenderJobs.IgnoreQueryFilters().ExecuteUpdateAsync(u => u.SetProperty(j => j.CancelRequested, true)).GetAwaiter().GetResult();

        await Processor().StartAsync(id);

        var job = Job(id);
        Assert.Equal(RenderJobStatus.Cancelled, job.Status);
        Assert.Single(_voice.Spoken); // the second scene was never spoken
        Assert.Empty(_store.Files);   // the first scene's voice file is gone
        Assert.Equal(2, _metering.Refunded.Count);
        Assert.Null(_renderer.Submitted);
    }

    // ----- isolation -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_script_and_videos_are_never_seen_started_cancelled_or_played()
    {
        var mine = await Script();
        var id = await Started(mine);

        _tenant.TenantId = _b;
        await Assert.ThrowsAsync<NotFoundException>(() => Service().StartAsync(_b, mine.Id, new StartRenderRequest(null, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ListAsync(_b, mine.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().GetAsync(_b, id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().CancelAsync(_b, id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().OpenVideoAsync(_b, id));
        _tenant.TenantId = _a;

        Assert.Equal(RenderJobStatus.Queued, Job(id).Status);
        Assert.Single(await Service().ListAsync(_a, mine.Id));
    }

    [Fact]
    public async Task A_deleted_project_cannot_have_a_video_made()
    {
        var version = await Script();
        (await _db.ContentProjects.SingleAsync()).IsDeleted = true;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Service().StartAsync(_a, version.Id, new StartRenderRequest(null, null)));
    }

    [Fact]
    public void The_endpoints_are_for_admins_and_managers_only_and_need_the_studio_switched_on()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ContentStudioVideoController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Api.Controllers.StudioRoles.Contributors, authorize.Roles);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(WhatsAppSalesAutomation.Api.Filters.RequiresFeatureAttribute)));
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));
    }

    [Fact]
    public void Languages_map_to_voice_codes_and_unknown_ones_are_read_in_indian_english()
    {
        Assert.Equal(("hi-IN", "hi-IN", "ta-IN", "en-IN", "en-IN"), (VoiceLanguages.CodeFor("Hindi"), VoiceLanguages.CodeFor("hinglish"), VoiceLanguages.CodeFor(" Tamil "), VoiceLanguages.CodeFor("Klingon"), VoiceLanguages.CodeFor(null)));
        Assert.Equal(2, new RenderOptions { MaxConcurrent = 0 }.ConcurrencyLimit + 1);
        Assert.Equal("stage", new RenderOptions { ShotstackEnvironment = "bogus" }.Environment);
        Assert.Equal("v1", new RenderOptions { ShotstackEnvironment = "V1" }.Environment);
    }
}
