using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Analytics;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// How published videos are doing, and the housekeeping that keeps the studio from leaking money or files: a daily snapshot per video (never
/// more often than every 20 hours), a video the platform stops returning is marked rather than zeroed, a sign-in the platform rejects flags the
/// account, stuck renders and publications are ended with their charges returned, old video files and dead sign-ins are removed, and the daily
/// jobs work one tenant at a time in that tenant's own scope. YouTube is a scripted fake.
/// </summary>
public sealed class VideoAnalyticsTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TenantScope : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
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
        public DateTime Now { get; set; } = Start;
        public DateTime UtcNow => Now;
        public DateTime IstNow => Now.AddHours(5.5);
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string plainText) => "enc:" + plainText;
        public string? TryUnprotect(string protectedValue) => protectedValue.StartsWith("enc:", StringComparison.Ordinal) ? protectedValue[4..] : null;
    }

    private sealed class MemoryStore : ITenantDocumentStore
    {
        public bool IsConfigured => true;
        public Dictionary<string, byte[]> Files { get; } = new();
        public bool FailDelete { get; set; }
        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            if (FailDelete)
                throw new InvalidOperationException("storage down");
            Files.Remove(storageKey);
            return Task.CompletedTask;
        }

        public string FolderOf(Guid tenantId) => $"private/{tenantId:N}/product-knowledge/";
    }

    private sealed class Queue : IPublicationQueue
    {
        public List<(Guid Id, TimeSpan? Delay)> Items { get; } = new();
        public void Enqueue(Guid publicationId, Guid tenantId, TimeSpan? delay) => Items.Add((publicationId, delay));
    }

    private sealed class Metering : IStudioMeteringService
    {
        public List<Guid> Refunded { get; } = new();
        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default)
        {
            Refunded.Add(activityId);
            return Task.CompletedTask;
        }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class YouTube : IYouTubeClient
    {
        public bool IsConfigured => true;
        public Dictionary<string, VideoStatistics> Stats { get; } = new();
        public Exception? StatsFail { get; set; }
        public Exception? RefreshFails { get; set; }
        public Exception? RevokeFails { get; set; }
        public List<(string Token, IReadOnlyList<string> Ids)> Asked { get; } = new();
        public List<string> Refreshes { get; } = new();
        public List<string> Revoked { get; } = new();

        public string BuildLoginUrl(string redirectUri, string state) => throw new NotSupportedException();
        public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            Refreshes.Add(refreshToken);
            return RefreshFails is null ? Task.FromResult(new OAuthTokens("access-2", null, Start.AddHours(5), "x")) : Task.FromException<OAuthTokens>(RefreshFails);
        }

        public Task<YouTubeChannel?> GetChannelAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> UploadAsync(string accessToken, YouTubeUploadRequest request, Stream video, long length, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RevokeAsync(string token, CancellationToken cancellationToken = default)
        {
            Revoked.Add(token);
            return RevokeFails is null ? Task.CompletedTask : Task.FromException(RevokeFails);
        }

        public Task<IReadOnlyDictionary<string, VideoStatistics>> GetVideoStatisticsAsync(string accessToken, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default)
        {
            Asked.Add((accessToken, videoIds));
            if (StatsFail is not null)
                return Task.FromException<IReadOnlyDictionary<string, VideoStatistics>>(StatsFail);
            return Task.FromResult<IReadOnlyDictionary<string, VideoStatistics>>(videoIds.Where(Stats.ContainsKey).ToDictionary(id => id, id => Stats[id]));
        }
    }

    private sealed class Retention : IOptionsSnapshot<StudioRetentionOptions>
    {
        public StudioRetentionOptions Value { get; set; } = new();
        public StudioRetentionOptions Get(string? name) => Value;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Clock _clock = new();
    private readonly MemoryStore _store = new();
    private readonly Queue _queue = new();
    private readonly Metering _metering = new();
    private readonly YouTube _youtube = new();
    private readonly Retention _retention = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public VideoAnalyticsTests()
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

    private VideoAnalyticsService Analytics() => new(_db, new Protector(), _youtube, _clock, NullLogger<VideoAnalyticsService>.Instance);

    private StudioMaintenanceService Maintenance() => new(_db, _store, _metering, _queue, _youtube, new Protector(), _clock, _retention, NullLogger<StudioMaintenanceService>.Instance);

    private async Task<SocialConnection> Account(Guid? tenant = null, SocialConnectionStatus status = SocialConnectionStatus.Connected, string id = "UC1")
    {
        var connection = new SocialConnection
        {
            TenantId = tenant ?? _a, Platform = SocialPlatform.YouTube, ExternalAccountId = id, AccountName = "Clinic Channel", Status = status, EncryptedAccessToken = "enc:access-1",
            EncryptedRefreshToken = "enc:refresh-1", TokenExpiresAtUtc = _clock.Now.AddHours(1), Scopes = "youtube.upload"
        };
        _db.SocialConnections.Add(connection);
        await _db.SaveChangesAsync();
        return connection;
    }

    private async Task<Publication> Post(SocialConnection connection, string videoId, PublicationStatus status = PublicationStatus.Published, DateTime? publishedAt = null, string title = "A video")
    {
        var publication = new Publication
        {
            TenantId = connection.TenantId, ProjectId = Guid.NewGuid(), VersionId = Guid.NewGuid(), RenderJobId = Guid.NewGuid(), ConnectionId = connection.Id, Platform = SocialPlatform.YouTube,
            AccountName = connection.AccountName, Status = status, Title = title, IdempotencyKey = Guid.NewGuid().ToString("N"), ApprovedHash = new string('a', 64),
            ExternalPostId = status == PublicationStatus.Published ? videoId : null, Url = status == PublicationStatus.Published ? $"https://www.youtube.com/watch?v={videoId}" : null,
            PublishedAtUtc = status == PublicationStatus.Published ? publishedAt ?? _clock.Now.AddDays(-3) : null
        };
        _db.Publications.Add(publication);
        await _db.SaveChangesAsync();
        return publication;
    }

    // ----- the daily sync --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_published_video_gets_a_snapshot_of_its_numbers_and_only_published_recent_ones_are_asked_about()
    {
        var account = await Account();
        var one = await Post(account, "vid1");
        await Post(account, "vid2");
        await Post(account, "vidFailed", PublicationStatus.Failed);
        await Post(account, "vidOld", publishedAt: _clock.Now.AddDays(-AnalyticsLimits.MetricsWindowDays - 1));
        _youtube.Stats["vid1"] = new VideoStatistics(120, 9, 3);
        _youtube.Stats["vid2"] = new VideoStatistics(5, 0, 0);

        var result = await Analytics().SyncTenantAsync(_a);

        Assert.Equal((2, 0, 0, 0), (result.Updated, result.Missing, result.Skipped, result.Errors));
        var asked = Assert.Single(_youtube.Asked);
        Assert.Equal(new[] { "vid1", "vid2" }, asked.Ids.OrderBy(i => i));
        Assert.Equal("access-1", asked.Token);
        var snapshot = await _db.PublicationMetricSnapshots.SingleAsync(m => m.PublicationId == one.Id);
        Assert.Equal((120L, 9L, 3L, _clock.Now), (snapshot.Views, snapshot.Likes, snapshot.Comments, snapshot.CapturedAtUtc));
        Assert.Equal(_clock.Now, (await _db.Publications.SingleAsync(p => p.Id == one.Id)).MetricsRefreshedAtUtc);
    }

    [Fact]
    public async Task A_video_asked_about_in_the_last_day_is_left_alone_and_asked_again_after_it_so_the_history_grows()
    {
        var account = await Account();
        var post = await Post(account, "vid1");
        _youtube.Stats["vid1"] = new VideoStatistics(10, 1, 0);

        await Analytics().SyncTenantAsync(_a);
        _clock.Now = _clock.Now.AddHours(5);
        var again = await Analytics().SyncTenantAsync(_a);
        Assert.Equal(0, again.Updated);
        Assert.Single(_youtube.Asked);

        _clock.Now = _clock.Now.Add(AnalyticsLimits.SyncEvery);
        _youtube.Stats["vid1"] = new VideoStatistics(25, 2, 1);
        await Analytics().SyncTenantAsync(_a);

        var series = await _db.PublicationMetricSnapshots.Where(m => m.PublicationId == post.Id).OrderBy(m => m.CapturedAtUtc).Select(m => m.Views).ToListAsync();
        Assert.Equal(new long[] { 10, 25 }, series);
    }

    [Fact]
    public async Task Videos_are_asked_about_fifty_at_a_time()
    {
        var account = await Account();
        for (var i = 0; i < 60; i++)
            await Post(account, $"v{i}");

        var result = await Analytics().SyncTenantAsync(_a);

        Assert.Equal(new[] { 50, 10 }, _youtube.Asked.Select(a => a.Ids.Count));
        Assert.Equal(60, result.Missing + result.Updated);
    }

    [Fact]
    public async Task A_video_the_platform_no_longer_returns_is_marked_not_zeroed_and_unmarked_if_it_returns()
    {
        var account = await Account();
        var post = await Post(account, "vid1");
        _youtube.Stats["vid1"] = new VideoStatistics(40, 4, 2);
        await Analytics().SyncTenantAsync(_a);

        _clock.Now = _clock.Now.AddDays(1);
        _youtube.Stats.Clear();
        var result = await Analytics().SyncTenantAsync(_a);

        Assert.Equal((0, 1), (result.Updated, result.Missing));
        var row = await _db.Publications.SingleAsync();
        Assert.Equal(_clock.Now, row.MissingOnPlatformSinceUtc);
        Assert.Single(await _db.PublicationMetricSnapshots.ToListAsync()); // no fake zero snapshot
        var item = Assert.Single((await Analytics().GetSummaryAsync(_a)).Items);
        Assert.Equal((40L, true), (item.Views, item.MissingOnPlatform)); // the last numbers it gave

        _clock.Now = _clock.Now.AddDays(1);
        _youtube.Stats["vid1"] = new VideoStatistics(50, 5, 2);
        await Analytics().SyncTenantAsync(_a);
        Assert.Null((await _db.Publications.SingleAsync(p => p.Id == post.Id)).MissingOnPlatformSinceUtc);
    }

    [Fact]
    public async Task A_token_about_to_run_out_is_renewed_first()
    {
        var account = await Account();
        account.TokenExpiresAtUtc = _clock.Now.AddSeconds(20);
        await _db.SaveChangesAsync();
        await Post(account, "vid1");
        _youtube.Stats["vid1"] = new VideoStatistics(1, 0, 0);

        await Analytics().SyncTenantAsync(_a);

        Assert.Equal(new[] { "refresh-1" }, _youtube.Refreshes);
        Assert.Equal("access-2", _youtube.Asked.Single().Token);
    }

    [Fact]
    public async Task A_sign_in_the_platform_rejects_flags_the_account_and_records_nothing_while_a_temporary_problem_is_simply_tried_again_tomorrow()
    {
        var account = await Account();
        var post = await Post(account, "vid1");

        _youtube.StatsFail = new SocialProviderException("Google had a problem on its side.", SocialFailure.Transient);
        var temporary = await Analytics().SyncTenantAsync(_a);
        Assert.Equal(1, temporary.Errors);
        Assert.Null((await _db.Publications.SingleAsync()).MetricsRefreshedAtUtc); // still due
        Assert.Equal(SocialConnectionStatus.Connected, (await _db.SocialConnections.SingleAsync()).Status);

        _youtube.StatsFail = new SocialProviderException("Google no longer accepts the saved sign-in.", SocialFailure.NeedsReconnect);
        await Analytics().SyncTenantAsync(_a);
        var connection = await _db.SocialConnections.SingleAsync();
        Assert.Equal(SocialConnectionStatus.NeedsReconnect, connection.Status);
        Assert.NotNull(connection.LastError);

        var skipped = await Analytics().SyncTenantAsync(_a); // flagged accounts are not asked at all
        Assert.Equal((0, 1), (skipped.Errors, skipped.Skipped));
        Assert.Empty(await _db.PublicationMetricSnapshots.ToListAsync());
        Assert.NotNull(post);
    }

    [Fact]
    public async Task A_network_failure_is_counted_not_thrown()
    {
        var account = await Account();
        await Post(account, "vid1");
        _youtube.StatsFail = new HttpRequestException("down");

        var result = await Analytics().SyncTenantAsync(_a);

        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task Only_tenants_with_recently_published_videos_are_listed_for_the_sync()
    {
        await Post(await Account(), "vid1");
        await Post(await Account(_b, id: "UC2"), "vidOld", publishedAt: _clock.Now.AddDays(-400));
        await Post(await Account(Guid.NewGuid(), id: "UC3"), "vidFailed", PublicationStatus.Failed);

        Assert.Equal(new[] { _a }, await Analytics().TenantsToSyncAsync());
    }

    // ----- the summary -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_summary_adds_up_the_latest_numbers_keeps_each_videos_history_and_counts_the_last_thirty_days()
    {
        var account = await Account();
        var first = await Post(account, "vid1", publishedAt: _clock.Now.AddDays(-5), title: "First");
        var second = await Post(account, "vid2", publishedAt: _clock.Now.AddDays(-2), title: "Second");
        await Post(account, "vidWaiting", PublicationStatus.Scheduled);
        await Post(account, "vidFailed", PublicationStatus.Failed);
        _youtube.Stats["vid1"] = new VideoStatistics(100, 10, 1);
        _youtube.Stats["vid2"] = new VideoStatistics(7, 1, 0);
        await Analytics().SyncTenantAsync(_a);
        _clock.Now = _clock.Now.AddDays(1);
        _youtube.Stats["vid1"] = new VideoStatistics(150, 12, 2);
        await Analytics().SyncTenantAsync(_a);

        var summary = await Analytics().GetSummaryAsync(_a);

        Assert.Equal((2, 1, 1), (summary.Published, summary.WaitingOrSending, summary.FailedLast30Days));
        Assert.Equal((157L, 13L, 2L), (summary.Views, summary.Likes, summary.Comments)); // the latest of each, added up
        Assert.Equal(_clock.Now, summary.LastUpdatedUtc);
        Assert.Equal(new[] { "Second", "First" }, summary.Items.Select(i => i.Title)); // newest first
        var history = summary.Items.Single(i => i.Id == first.Id).Series;
        Assert.Equal(new long[] { 100, 150 }, history.Select(p => p.Views));
        Assert.Equal(new long[] { 7, 7 }, summary.Items.Single(i => i.Id == second.Id).Series.Select(p => p.Views)); // asked again a day later, unchanged
        Assert.Equal(2, summary.Last30Days.VideosPublished);
    }

    [Fact]
    public async Task The_summary_funnel_counts_scripts_approvals_and_finished_videos_but_not_test_mode_ones()
    {
        var project = new ContentProject { TenantId = _a, Title = "P", Goal = "G" };
        _db.ContentProjects.Add(project);
        _db.ContentVersions.AddRange(
            new ContentVersion { TenantId = _a, ProjectId = project.Id, Number = 1, PromptVersion = "v", Provider = "OpenAI", ContentHash = new string('a', 64), Status = ContentVersionStatus.Approved, ReviewedAt = _clock.Now.AddDays(-1) },
            new ContentVersion { TenantId = _a, ProjectId = project.Id, Number = 2, PromptVersion = "v", Provider = "OpenAI", ContentHash = new string('a', 64) },
            new ContentVersion { TenantId = _a, ProjectId = project.Id, Number = 3, PromptVersion = "v", Provider = "Simulator", ContentHash = new string('a', 64), IsSimulated = true });
        _db.RenderJobs.AddRange(
            new RenderJob { TenantId = _a, ProjectId = project.Id, VersionId = Guid.NewGuid(), Status = RenderJobStatus.Succeeded, Renderer = "Shotstack", ContentHash = new string('a', 64), FinishedAt = _clock.Now.AddDays(-2) },
            new RenderJob { TenantId = _a, ProjectId = project.Id, VersionId = Guid.NewGuid(), Status = RenderJobStatus.Succeeded, Renderer = "Simulator", IsSimulated = true, ContentHash = new string('a', 64), FinishedAt = _clock.Now.AddDays(-2) });
        await _db.SaveChangesAsync();

        var funnel = (await Analytics().GetSummaryAsync(_a)).Last30Days;

        Assert.Equal((2, 1, 1, 0), (funnel.ScriptsWritten, funnel.ScriptsApproved, funnel.VideosMade, funnel.VideosPublished));
    }

    [Fact]
    public async Task Another_tenants_videos_never_appear_and_nobody_else_can_refresh_them()
    {
        var mine = await Post(await Account(), "vid1");
        var theirs = await Post(await Account(_b, id: "UC2"), "vidTheirs");
        _youtube.Stats["vid1"] = new VideoStatistics(1, 0, 0);
        _youtube.Stats["vidTheirs"] = new VideoStatistics(99, 9, 9);
        await Analytics().SyncTenantAsync(_a);

        var summary = await Analytics().GetSummaryAsync(_a);

        Assert.Equal(mine.Id, Assert.Single(summary.Items).Id);
        Assert.Equal(1L, summary.Views);
        Assert.DoesNotContain("vidTheirs", _youtube.Asked.SelectMany(a => a.Ids));
        _tenant.TenantId = _b;
        await Assert.ThrowsAsync<NotFoundException>(() => Analytics().RefreshAsync(_b, mine.Id));
        Assert.Equal(theirs.Id, Assert.Single((await Analytics().GetSummaryAsync(_b)).Items).Id);
    }

    // ----- refreshing one post now -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_post_can_be_refreshed_now_but_not_again_within_fifteen_minutes()
    {
        var post = await Post(await Account(), "vid1");
        _youtube.Stats["vid1"] = new VideoStatistics(10, 1, 0);

        var first = await Analytics().RefreshAsync(_a, post.Id);
        Assert.Equal(10L, first.Views);

        _clock.Now = _clock.Now.AddMinutes(5);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Analytics().RefreshAsync(_a, post.Id));
        Assert.Contains("5 minute(s) ago", ex.Message);
        Assert.Contains("10 minute(s)", ex.Message);

        _clock.Now = _clock.Now.AddMinutes(11);
        _youtube.Stats["vid1"] = new VideoStatistics(30, 2, 0);
        Assert.Equal(30L, (await Analytics().RefreshAsync(_a, post.Id)).Views);
    }

    [Fact]
    public async Task A_post_that_is_not_on_the_platform_or_cannot_be_asked_about_gives_a_clear_refusal()
    {
        var account = await Account();
        var waiting = await Post(account, "x", PublicationStatus.Scheduled);
        Assert.Contains("not on YouTube", (await Assert.ThrowsAsync<ConflictException>(() => Analytics().RefreshAsync(_a, waiting.Id))).Message);

        var post = await Post(account, "vid1");
        _youtube.StatsFail = new SocialProviderException("Google had a problem on its side.", SocialFailure.Transient);
        Assert.Contains("could not be asked", (await Assert.ThrowsAsync<ConflictException>(() => Analytics().RefreshAsync(_a, post.Id))).Message);
    }

    // ----- housekeeping ----------------------------------------------------------------------------------------------------------

    private async Task<RenderJob> Render(RenderJobStatus status, TimeSpan age, Guid? tenant = null, bool withFile = false, bool withCharges = true)
    {
        var tenantId = tenant ?? _a;
        var job = new RenderJob
        {
            TenantId = tenantId, ProjectId = Guid.NewGuid(), VersionId = Guid.NewGuid(), Status = status, Renderer = "Shotstack", ContentHash = new string('a', 64),
            StartedAt = _clock.Now - age, FinishedAt = status is RenderJobStatus.Succeeded or RenderJobStatus.Failed ? _clock.Now - age : null,
            RenderActivityId = withCharges ? Guid.NewGuid() : null, VoiceActivityId = withCharges ? Guid.NewGuid() : null
        };
        if (withFile)
        {
            job.OutputStorageKey = $"s3:private/{tenantId:N}/product-knowledge/studio/renders/{job.Id:N}.mp4";
            _store.Files[job.OutputStorageKey] = new byte[10];
        }

        _db.RenderJobs.Add(job);
        await _db.SaveChangesAsync();
        job.CreatedAt = _clock.Now - age; // for the "never started" case
        await _db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task A_render_that_stopped_moving_is_ended_with_its_charges_returned_and_its_voice_files_deleted_while_a_recent_one_is_left_alone()
    {
        var stuck = await Render(RenderJobStatus.Rendering, TimeSpan.FromHours(3));
        var key = $"s3:private/{_a:N}/product-knowledge/studio/audio/{stuck.Id:N}/scene-1.wav";
        _store.Files[key] = new byte[3];
        stuck.AudioKeysJson = System.Text.Json.JsonSerializer.Serialize(new[] { key });
        var recent = await Render(RenderJobStatus.Rendering, TimeSpan.FromMinutes(10));
        await _db.SaveChangesAsync();

        var result = await Maintenance().RunForTenantAsync(_a);

        Assert.Equal(1, result.StuckRenders);
        var ended = await _db.RenderJobs.AsNoTracking().SingleAsync(j => j.Id == stuck.Id);
        Assert.Equal(RenderJobStatus.Failed, ended.Status);
        Assert.Contains("Nothing was charged", ended.Error);
        Assert.Equal(new[] { stuck.RenderActivityId!.Value, stuck.VoiceActivityId!.Value }, _metering.Refunded);
        Assert.DoesNotContain(key, _store.Files.Keys);
        Assert.Equal(RenderJobStatus.Rendering, (await _db.RenderJobs.AsNoTracking().SingleAsync(j => j.Id == recent.Id)).Status);

        Assert.Equal(0, (await Maintenance().RunForTenantAsync(_a)).StuckRenders); // a second pass finds nothing more
    }

    [Fact]
    public async Task The_clip_files_of_a_stuck_clips_render_are_deleted_with_it()
    {
        var stuck = await Render(RenderJobStatus.Rendering, TimeSpan.FromHours(3));
        stuck.Mode = "Clips";
        var key = $"s3:private/{_a:N}/product-knowledge/studio/renders/{stuck.Id:N}/clip-1.mp4";
        _store.Files[key] = new byte[4];
        _db.RenderClips.Add(new RenderClip { TenantId = _a, JobId = stuck.Id, SceneId = Guid.NewGuid(), Order = 1, Status = RenderClipStatus.Done, OutputStorageKey = key, ExternalRenderId = "r1", Seconds = 10 });
        await _db.SaveChangesAsync();

        var result = await Maintenance().RunForTenantAsync(_a);

        Assert.Equal(1, result.StuckRenders);
        Assert.DoesNotContain(key, _store.Files.Keys);
        Assert.Null((await _db.RenderClips.AsNoTracking().SingleAsync()).OutputStorageKey);
    }

    [Fact]
    public async Task A_publication_stuck_mid_upload_is_marked_as_possibly_on_the_channel_and_one_that_never_started_is_sent_again()
    {
        var account = await Account();
        var uploading = await Post(account, "x", PublicationStatus.Uploading);
        uploading.StartedAtUtc = _clock.Now.AddHours(-3);
        var lost = await Post(account, "y", PublicationStatus.Scheduled);
        lost.ScheduledAtUtc = _clock.Now.AddHours(-3);
        var future = await Post(account, "z", PublicationStatus.Scheduled);
        future.ScheduledAtUtc = _clock.Now.AddHours(5);
        var running = await Post(account, "w", PublicationStatus.Uploading);
        running.StartedAtUtc = _clock.Now.AddMinutes(-5);
        await _db.SaveChangesAsync();

        var result = await Maintenance().RunForTenantAsync(_a);

        Assert.Equal((1, 1), (result.StuckPublications, result.RequeuedPublications));
        var failed = await _db.Publications.AsNoTracking().SingleAsync(p => p.Id == uploading.Id);
        Assert.Equal(PublicationStatus.Failed, failed.Status);
        Assert.Contains("may or may not be on YouTube", failed.LastError);
        Assert.Equal((lost.Id, (TimeSpan?)null), Assert.Single(_queue.Items)); // the lost one only; the failed one is never retried by itself
        Assert.Equal(PublicationStatus.Scheduled, (await _db.Publications.AsNoTracking().SingleAsync(p => p.Id == future.Id)).Status);
        Assert.Equal(PublicationStatus.Uploading, (await _db.Publications.AsNoTracking().SingleAsync(p => p.Id == running.Id)).Status);
    }

    [Fact]
    public async Task Video_files_past_their_retention_are_deleted_but_the_record_stays_and_a_file_still_waiting_to_be_published_is_kept()
    {
        var old = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(100), withFile: true, withCharges: false);
        var young = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(10), withFile: true, withCharges: false);
        var needed = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(100), withFile: true, withCharges: false);
        var account = await Account();
        (await Post(account, "p", PublicationStatus.Scheduled)).RenderJobId = needed.Id;
        await _db.SaveChangesAsync();

        var result = await Maintenance().RunForTenantAsync(_a);

        Assert.Equal(1, result.VideosRemoved);
        var removed = await _db.RenderJobs.AsNoTracking().SingleAsync(j => j.Id == old.Id);
        Assert.Equal((null, 0L, RenderJobStatus.Succeeded), (removed.OutputStorageKey, removed.OutputBytes, removed.Status));
        Assert.Contains("removed after 90 days", removed.Stage);
        Assert.DoesNotContain(_store.Files.Keys, k => k.Contains(old.Id.ToString("N")));
        Assert.Contains(_store.Files.Keys, k => k.Contains(young.Id.ToString("N")));
        Assert.Contains(_store.Files.Keys, k => k.Contains(needed.Id.ToString("N")));
    }

    [Fact]
    public async Task Retention_can_be_switched_off_and_never_goes_below_a_week()
    {
        var recent = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(3), withFile: true, withCharges: false);
        var ten = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(10), withFile: true, withCharges: false);

        _retention.Value = new StudioRetentionOptions { VideoDays = 0 };
        Assert.Equal(0, (await Maintenance().RunForTenantAsync(_a)).VideosRemoved);

        _retention.Value = new StudioRetentionOptions { VideoDays = 1 }; // treated as 7
        Assert.Equal(1, (await Maintenance().RunForTenantAsync(_a)).VideosRemoved);
        Assert.Contains(_store.Files.Keys, k => k.Contains(recent.Id.ToString("N")));
        Assert.DoesNotContain(_store.Files.Keys, k => k.Contains(ten.Id.ToString("N")));
    }

    [Fact]
    public async Task A_file_outside_the_tenants_folder_is_never_deleted()
    {
        var job = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(100), withCharges: false);
        var theirs = $"s3:private/{_b:N}/product-knowledge/studio/renders/theirs.mp4";
        job.OutputStorageKey = theirs;
        _store.Files[theirs] = new byte[1];
        await _db.SaveChangesAsync();

        await Maintenance().RunForTenantAsync(_a);

        Assert.Contains(theirs, _store.Files.Keys); // the row is cleaned up, the foreign file is not touched
    }

    [Fact]
    public async Task A_sign_in_that_needed_reconnecting_for_too_long_is_revoked_and_deleted_and_what_waited_on_it_is_cancelled()
    {
        var dead = await Account(status: SocialConnectionStatus.NeedsReconnect, id: "UC1");
        dead.UpdatedAt = _clock.Now.AddDays(-100);
        var fresh = await Account(status: SocialConnectionStatus.NeedsReconnect, id: "UC2");
        fresh.UpdatedAt = _clock.Now.AddDays(-10);
        var healthy = await Account(id: "UC3");
        healthy.UpdatedAt = _clock.Now.AddDays(-300);
        var waiting = await Post(dead, "x", PublicationStatus.Scheduled);
        await _db.SaveChangesAsync();
        _youtube.RevokeFails = new HttpRequestException("down");

        var result = await Maintenance().RunForTenantAsync(_a);

        Assert.Equal(1, result.SignInsRemoved);
        Assert.Equal(new[] { "refresh-1" }, _youtube.Revoked);
        var left = await _db.SocialConnections.AsNoTracking().Select(c => c.ExternalAccountId).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { "UC2", "UC3" }, left); // revoking failing did not keep the tokens
        var cancelled = await _db.Publications.AsNoTracking().SingleAsync(p => p.Id == waiting.Id);
        Assert.Equal(PublicationStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task One_step_failing_does_not_stop_the_others_and_nothing_of_another_tenant_is_touched()
    {
        var theirs = await Render(RenderJobStatus.Rendering, TimeSpan.FromHours(5), _b);
        var mineStuck = await Render(RenderJobStatus.Rendering, TimeSpan.FromHours(5));
        var old = await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(100), withFile: true, withCharges: false);
        var dead = await Account(status: SocialConnectionStatus.NeedsReconnect);
        dead.UpdatedAt = _clock.Now.AddDays(-100);
        await _db.SaveChangesAsync();
        _store.FailDelete = true; // the video-file step cannot delete anything

        var result = await Maintenance().RunForTenantAsync(_a);

        Assert.Equal((1, 0, 1), (result.StuckRenders, result.VideosRemoved, result.SignInsRemoved));
        Assert.NotNull((await _db.RenderJobs.AsNoTracking().SingleAsync(j => j.Id == old.Id)).OutputStorageKey); // kept: tried again tomorrow
        Assert.Equal(RenderJobStatus.Failed, (await _db.RenderJobs.AsNoTracking().SingleAsync(j => j.Id == mineStuck.Id)).Status);
        Assert.Equal(RenderJobStatus.Rendering, (await _db.RenderJobs.AsNoTracking().IgnoreQueryFilters().SingleAsync(j => j.Id == theirs.Id)).Status);
    }

    [Fact]
    public async Task The_tenants_to_look_after_are_those_with_any_studio_work_or_accounts()
    {
        await Render(RenderJobStatus.Succeeded, TimeSpan.FromDays(1), _a, withCharges: false);
        await Account(_b, id: "UC9");

        var tenants = await Maintenance().TenantsAsync();

        Assert.Equal(new[] { _a, _b }.OrderBy(t => t), tenants.OrderBy(t => t));
    }

    // ----- the daily jobs --------------------------------------------------------------------------------------------------------

    private sealed class RecordingAnalytics : IVideoAnalyticsService
    {
        private readonly ITenantContext _tenant;
        public static List<(Guid Called, Guid? Saw)> Calls { get; } = new();
        public static Guid FailFor { get; set; }
        public static IReadOnlyList<Guid> Tenants { get; set; } = Array.Empty<Guid>();

        public RecordingAnalytics(ITenantContext tenant) => _tenant = tenant;

        public Task<IReadOnlyList<Guid>> TenantsToSyncAsync(CancellationToken cancellationToken = default) => Task.FromResult(Tenants);

        public Task<MetricsSyncResult> SyncTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            Calls.Add((tenantId, _tenant.TenantId));
            if (tenantId == FailFor)
                throw new InvalidOperationException("boom");
            return Task.FromResult(new MetricsSyncResult(1, 0, 0, 0));
        }

        public Task<AnalyticsSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicationPerformanceDto> RefreshAsync(Guid tenantId, Guid publicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_daily_metrics_job_runs_each_tenant_in_its_own_scope_with_its_own_tenant_and_one_failing_never_stops_the_next()
    {
        RecordingAnalytics.Calls.Clear();
        RecordingAnalytics.Tenants = new[] { _a, _b, Guid.NewGuid() };
        RecordingAnalytics.FailFor = _a;
        var services = new ServiceCollection();
        services.AddScoped<TenantScope>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantScope>());
        services.AddScoped<IVideoAnalyticsService, RecordingAnalytics>();
        using var provider = services.BuildServiceProvider();

        await new PublicationMetricsJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PublicationMetricsJob>.Instance).RunAsync();

        Assert.Equal(3, RecordingAnalytics.Calls.Count);
        Assert.All(RecordingAnalytics.Calls, c => Assert.Equal(c.Called, c.Saw)); // every call saw exactly its own tenant
    }

    [Fact]
    public void The_retention_settings_are_platform_settings_with_a_ninety_day_default_and_a_one_week_minimum()
    {
        var all = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All;
        Assert.Equal("90", all.Single(d => d.Key == "ContentStudio:Retention:VideoDays").DefaultValue);
        Assert.Equal("90", all.Single(d => d.Key == "ContentStudio:Retention:DeadSignInDays").DefaultValue);
        Assert.Null(StudioRetentionOptions.Effective(0));
        Assert.Equal(7, StudioRetentionOptions.Effective(2));
        Assert.Equal(90, StudioRetentionOptions.Effective(90));
    }

    [Fact]
    public void The_endpoints_are_for_admins_and_managers_and_need_the_studio_switched_on()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ContentStudioAnalyticsController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Api.Controllers.StudioRoles.Contributors, authorize.Roles);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(WhatsAppSalesAutomation.Api.Filters.RequiresFeatureAttribute)));
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));
    }
}
