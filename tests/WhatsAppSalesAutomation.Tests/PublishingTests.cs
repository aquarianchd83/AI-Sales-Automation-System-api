using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;
using WhatsAppSalesAutomation.Application.ContentStudio.Review;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Publishing an approved video to YouTube: the sign-in is bound to the tenant that started it; tokens are stored encrypted; nothing goes out
/// unless the video is that of an approved, unchanged script (checked when asked and again before sending); the same click is one publication;
/// a video YouTube already has is never sent twice; temporary problems retry with growing waits while refusals, lost connections mid-upload and
/// dead sign-ins do not; and one tenant never sees or touches another's accounts or posts. YouTube is a scripted fake.
/// </summary>
public sealed class PublishingTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

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
        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Files[storageKey]));
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string FolderOf(Guid tenantId) => $"private/{tenantId:N}/product-knowledge/";
    }

    private sealed class Queue : IPublicationQueue
    {
        public List<(Guid Id, TimeSpan? Delay)> Items { get; } = new();
        public void Enqueue(Guid publicationId, Guid tenantId, TimeSpan? delay) => Items.Add((publicationId, delay));
    }

    private sealed class Audit : IAuditTrailWriter
    {
        public List<(string Entity, AuditAction Action)> Rows { get; } = new();

        public Task RecordAsync(Guid tenantId, string entityName, Guid entityId, AuditAction action, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
        {
            Rows.Add((entityName, action));
            return Task.CompletedTask;
        }
    }

    private sealed class Options : IOptionsSnapshot<SocialOptions>
    {
        public SocialOptions Value { get; set; } = new() { GoogleClientId = "id", GoogleClientSecret = "secret", MaxPublishesPerDay = 5 };
        public SocialOptions Get(string? name) => Value;
    }

    private sealed class YouTube : IYouTubeClient
    {
        public bool IsConfigured { get; set; } = true;
        public OAuthTokens Exchanged { get; set; } = new("access-1", "refresh-1", Start.AddHours(1), "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly");
        public YouTubeChannel? Channel { get; set; } = new("UC123", "Clinic Channel");
        public OAuthTokens Refreshed { get; set; } = new("access-2", null, Start.AddHours(2), "x");
        public Exception? RefreshFails { get; set; }
        public Queue<Exception?> UploadOutcomes { get; } = new();
        public Exception? RevokeFails { get; set; }
        public List<string> Refreshes { get; } = new();
        public List<(string Token, YouTubeUploadRequest Request, long Length, long Read)> Uploads { get; } = new();
        public List<string> Revoked { get; } = new();

        public string BuildLoginUrl(string redirectUri, string state) => $"https://accounts.example/auth?redirect={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";
        public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default) => Task.FromResult(Exchanged);

        public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            Refreshes.Add(refreshToken);
            return RefreshFails is null ? Task.FromResult(Refreshed) : Task.FromException<OAuthTokens>(RefreshFails);
        }

        public Task<YouTubeChannel?> GetChannelAsync(string accessToken, CancellationToken cancellationToken = default) => Task.FromResult(Channel);

        public async Task<string> UploadAsync(string accessToken, YouTubeUploadRequest request, Stream video, long length, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await video.CopyToAsync(copy, cancellationToken);
            Uploads.Add((accessToken, request, length, copy.Length));
            var outcome = UploadOutcomes.Count > 0 ? UploadOutcomes.Dequeue() : null;
            if (outcome is not null)
                throw outcome;
            return "vid" + Uploads.Count;
        }

        public Task RevokeAsync(string token, CancellationToken cancellationToken = default)
        {
            Revoked.Add(token);
            return RevokeFails is null ? Task.CompletedTask : Task.FromException(RevokeFails);
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TenantScope _tenant = new();
    private readonly Clock _clock = new();
    private readonly Protector _protector = new();
    private readonly MemoryStore _store = new();
    private readonly Queue _queue = new();
    private readonly Audit _audit = new();
    private readonly Options _options = new();
    private readonly YouTube _youtube = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public PublishingTests()
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

    private PublishingService Service() => new(_db, _protector, _youtube, _queue, _options, new Admin(), _clock, _audit, NullLogger<PublishingService>.Instance);

    private PublicationProcessor Processor() => new(_db, _protector, _youtube, _store, _queue, _clock, NullLogger<PublicationProcessor>.Instance);

    private sealed record Setup(ContentVersion Version, RenderJob Render, SocialConnection Connection);

    /// <summary>An approved script with its matching finished video and a connected channel.</summary>
    private async Task<Setup> Ready(Guid? tenant = null, string? attribution = null)
    {
        var tenantId = tenant ?? _a;
        var project = new ContentProject { TenantId = tenantId, Title = "Cataract awareness", Goal = "Bookings" };
        var version = new ContentVersion { TenantId = tenantId, ProjectId = project.Id, Number = 2, PromptVersion = "v", Provider = "OpenAI", ContentHash = new string('a', 64), Status = ContentVersionStatus.Approved };
        var scenes = new List<ContentScene>();
        var assets = new Dictionary<Guid, StudioAsset>();
        for (var i = 1; i <= 2; i++)
        {
            var asset = new StudioAsset
            {
                TenantId = tenantId, Kind = attribution is null ? StudioAssetKind.Upload : StudioAssetKind.Stock, Name = $"Pic {i}", ContentType = "image/png", StorageKey = $"s3:k{i}",
                Checksum = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), LicenseName = "x", Attribution = i == 1 ? attribution : null
            };
            assets[asset.Id] = asset;
            scenes.Add(new ContentScene { TenantId = tenantId, VersionId = version.Id, Order = i, Narration = $"Scene {i}.", OnScreenText = "Hi", DurationSeconds = 8, ChosenAssetId = asset.Id });
        }

        version.ApprovedHash = ContentSnapshot.Compute(scenes, assets);
        var key = $"s3:private/{tenantId:N}/product-knowledge/studio/renders/{Guid.NewGuid():N}.mp4";
        _store.Files[key] = new byte[2048];
        var render = new RenderJob
        {
            TenantId = tenantId, ProjectId = project.Id, VersionId = version.Id, Status = RenderJobStatus.Succeeded, Renderer = "Shotstack", ContentHash = version.ApprovedHash, OutputStorageKey = key,
            OutputBytes = 2048
        };
        var connection = new SocialConnection
        {
            TenantId = tenantId, Platform = SocialPlatform.YouTube, ExternalAccountId = "UC123", AccountName = "Clinic Channel", EncryptedAccessToken = "enc:access-1", EncryptedRefreshToken = "enc:refresh-1",
            TokenExpiresAtUtc = _clock.Now.AddHours(1), Scopes = "youtube.upload"
        };
        _db.ContentProjects.Add(project);
        _db.ContentVersions.Add(version);
        _db.StudioAssets.AddRange(assets.Values);
        _db.ContentScenes.AddRange(scenes);
        _db.RenderJobs.Add(render);
        _db.SocialConnections.Add(connection);
        await _db.SaveChangesAsync();
        return new Setup(version, render, connection);
    }

    private static PublishVideoRequest Request(Setup s, string? key = "click-1", string? privacy = null, DateTime? at = null, string title = "Cataract surgery explained") =>
        new(s.Render.Id, s.Connection.Id, title, "  A short explainer.  ", privacy, at, key);

    private Publication Fresh(Guid id) => _db.Publications.AsNoTracking().IgnoreQueryFilters().Single(p => p.Id == id);

    // ----- connecting ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sign_in_is_started_only_when_the_platform_is_set_up_for_a_safe_return_address_and_carries_a_state_bound_to_the_tenant()
    {
        var url = (await Service().GetConnectUrlAsync(_a, "https://app.example/content-studio/connections")).Url;
        Assert.Contains("redirect=https%3A%2F%2Fapp.example%2Fcontent-studio%2Fconnections", url);
        var state = Uri.UnescapeDataString(url.Split("state=")[1]);
        Assert.StartsWith($"enc:{_a:N}|", state);

        await Assert.ThrowsAsync<ValidationException>(() => Service().GetConnectUrlAsync(_a, "http://evil.example/x"));
        await Assert.ThrowsAsync<ValidationException>(() => Service().GetConnectUrlAsync(_a, "not a url"));
        await Service().GetConnectUrlAsync(_a, "http://localhost:4200/content-studio/connections"); // development

        _youtube.IsConfigured = false;
        Assert.Contains("not set up", (await Assert.ThrowsAsync<ConflictException>(() => Service().GetConnectUrlAsync(_a, "https://app.example/x"))).Message);
    }

    private async Task<string> StateFor(Guid tenant, TimeSpan? life = null)
    {
        var url = (await Service().GetConnectUrlAsync(tenant, "https://app.example/x")).Url;
        return Uri.UnescapeDataString(url.Split("state=")[1]);
    }

    [Fact]
    public async Task Connecting_saves_the_channel_with_its_tokens_encrypted_and_never_returns_them()
    {
        var state = await StateFor(_a);

        var dto = await Service().ConnectAsync(_a, new CompleteConnectRequest("code", state, "https://app.example/x"));

        Assert.Equal(("YouTube", "Clinic Channel", "Connected"), (dto.Platform, dto.AccountName, dto.Status));
        var row = await _db.SocialConnections.SingleAsync();
        Assert.Equal(("enc:access-1", "enc:refresh-1", "UC123"), (row.EncryptedAccessToken, row.EncryptedRefreshToken, row.ExternalAccountId));
        Assert.DoesNotContain(typeof(ConnectionDto).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains((nameof(SocialConnection), AuditAction.Create), _audit.Rows);

        // Connecting the same channel again updates the one row and clears a "reconnect" mark.
        row.Status = SocialConnectionStatus.NeedsReconnect;
        row.LastError = "old";
        await _db.SaveChangesAsync();
        _youtube.Exchanged = new OAuthTokens("access-9", "refresh-9", Start.AddHours(1), _youtube.Exchanged.Scopes);
        await Service().ConnectAsync(_a, new CompleteConnectRequest("code", await StateFor(_a), "https://app.example/x"));
        var again = await _db.SocialConnections.SingleAsync();
        Assert.Equal(("enc:access-9", SocialConnectionStatus.Connected, null), (again.EncryptedAccessToken, again.Status, again.LastError));
    }

    [Fact]
    public async Task A_state_from_another_tenant_an_expired_one_or_a_made_up_one_connects_nothing()
    {
        var theirs = await StateFor(_b);
        var expired = await StateFor(_a);
        _clock.Now = _clock.Now.Add(PublishingLimits.StateLifetime).AddMinutes(1);
        var fresh = await StateFor(_a);

        foreach (var bad in new[] { theirs, expired, "enc:garbage", "not-ours", "" })
            await Assert.ThrowsAsync<ValidationException>(() => Service().ConnectAsync(_a, new CompleteConnectRequest("code", bad, "https://app.example/x")));
        await Assert.ThrowsAsync<ValidationException>(() => Service().ConnectAsync(_a, new CompleteConnectRequest("", fresh, "https://app.example/x")));

        Assert.Empty(await _db.SocialConnections.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Connecting_needs_long_term_access_the_upload_permission_and_a_channel()
    {
        var state = await StateFor(_a);
        _youtube.Exchanged = new OAuthTokens("a", null, Start.AddHours(1), "youtube.upload");
        Assert.Contains("long-term access", (await Assert.ThrowsAsync<ConflictException>(() => Service().ConnectAsync(_a, new CompleteConnectRequest("c", state, "https://app.example/x")))).Message);

        _youtube.Exchanged = new OAuthTokens("a", "r", Start.AddHours(1), "https://www.googleapis.com/auth/youtube.readonly");
        Assert.Contains("Uploading videos was not allowed", (await Assert.ThrowsAsync<ConflictException>(() => Service().ConnectAsync(_a, new CompleteConnectRequest("c", state, "https://app.example/x")))).Message);

        _youtube.Exchanged = new OAuthTokens("a", "r", Start.AddHours(1), "youtube.upload");
        _youtube.Channel = null;
        Assert.Contains("no YouTube channel", (await Assert.ThrowsAsync<ConflictException>(() => Service().ConnectAsync(_a, new CompleteConnectRequest("c", state, "https://app.example/x")))).Message);

        Assert.Empty(await _db.SocialConnections.ToListAsync());
    }

    [Fact]
    public async Task Disconnecting_cancels_what_was_waiting_revokes_access_deletes_the_tokens_and_does_not_depend_on_google_answering()
    {
        var setup = await Ready();
        var waiting = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, at: Start.AddDays(2)));
        _youtube.RevokeFails = new HttpRequestException("down");

        await Service().DisconnectAsync(_a, setup.Connection.Id);

        Assert.Empty(await _db.SocialConnections.ToListAsync());
        Assert.Equal(new[] { "refresh-1" }, _youtube.Revoked);
        var cancelled = Fresh(waiting.Id);
        Assert.Equal(PublicationStatus.Cancelled, cancelled.Status);
        Assert.Contains("disconnected", cancelled.LastError);
        Assert.Contains((nameof(SocialConnection), AuditAction.Delete), _audit.Rows);
        await Processor().RunAsync(waiting.Id); // its queued message arrives later and does nothing
        Assert.Empty(_youtube.Uploads);
    }

    // ----- asking to publish -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_video_is_queued_for_publishing_private_by_default_with_the_synthetic_voice_disclosed_and_the_credits_added()
    {
        var setup = await Ready(attribution: "Photo by Jane Doe on Pexels");

        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));

        Assert.Equal(("Scheduled", "private", "Clinic Channel", 2), (dto.Status, dto.Privacy, dto.AccountName, dto.VersionNumber));
        var row = Fresh(dto.Id);
        Assert.Equal((true, setup.Version.ApprovedHash, null), (row.DisclosesSyntheticMedia, row.ApprovedHash, row.ScheduledAtUtc));
        Assert.Equal("A short explainer.\n\nCredits:\n- Photo by Jane Doe on Pexels", row.Description);
        Assert.Equal((dto.Id, (TimeSpan?)null), Assert.Single(_queue.Items));
        Assert.Contains((nameof(Publication), AuditAction.Create), _audit.Rows);
    }

    [Fact]
    public async Task A_later_time_is_kept_and_queued_with_the_wait_while_a_time_that_has_passed_means_now()
    {
        var setup = await Ready();
        var later = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k1", at: Start.AddHours(3)));
        Assert.Equal(Start.AddHours(3), later.ScheduledAtUtc);
        Assert.Equal(TimeSpan.FromHours(3), _queue.Items.Single().Delay);

        await Service().CancelAsync(_a, later.Id);
        var now = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k2", at: Start.AddMinutes(-5)));
        Assert.Null(now.ScheduledAtUtc);
        Assert.Null(_queue.Items.Last().Delay);

        await Service().CancelAsync(_a, now.Id);
        await Assert.ThrowsAsync<ValidationException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k3", at: Start.AddDays(91))));
    }

    [Fact]
    public async Task Only_an_approved_unchanged_script_with_its_own_finished_video_can_be_published()
    {
        var setup = await Ready();

        // A draft.
        var stored = await _db.ContentVersions.SingleAsync();
        stored.Status = ContentVersionStatus.InReview;
        await _db.SaveChangesAsync();
        Assert.Contains("Only an approved script", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)))).Message);
        stored.Status = ContentVersionStatus.Approved;

        // Changed after approval.
        var scene = await _db.ContentScenes.OrderBy(s => s.Order).FirstAsync();
        scene.Narration = "Changed after approval.";
        await _db.SaveChangesAsync();
        Assert.Contains("no longer matches what was approved", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)))).Message);
        scene.Narration = "Scene 1.";
        await _db.SaveChangesAsync();

        // A video that is not of the approved content, unfinished, a test render, or of another script.
        var render = await _db.RenderJobs.SingleAsync();
        render.ContentHash = new string('0', 64);
        await _db.SaveChangesAsync();
        Assert.Contains("made before the script was approved", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)))).Message);
        render.ContentHash = setup.Version.ApprovedHash!;
        render.IsSimulated = true;
        await _db.SaveChangesAsync();
        Assert.Contains("test render", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)))).Message);
        render.IsSimulated = false;
        render.Status = RenderJobStatus.Rendering;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)));
        render.Status = RenderJobStatus.Succeeded;
        render.VersionId = Guid.NewGuid();
        await _db.SaveChangesAsync();
        Assert.Contains("Choose a video made from this script", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)))).Message);
        render.VersionId = setup.Version.Id;
        await _db.SaveChangesAsync();

        Assert.Empty(_queue.Items);
        await Service().PublishAsync(_a, setup.Version.Id, Request(setup)); // and with everything in order it goes through
    }

    [Fact]
    public async Task The_title_privacy_account_and_duplicates_are_checked()
    {
        var setup = await Ready();
        await Assert.ThrowsAsync<ValidationException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup, title: "   ")));
        await Assert.ThrowsAsync<ValidationException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup, privacy: "everyone")));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup) with { ConnectionId = Guid.NewGuid() }));

        (await _db.SocialConnections.SingleAsync()).Status = SocialConnectionStatus.NeedsReconnect;
        await _db.SaveChangesAsync();
        Assert.Contains("needs to be connected again", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup)))).Message);
        (await _db.SocialConnections.SingleAsync()).Status = SocialConnectionStatus.Connected;
        await _db.SaveChangesAsync();

        var first = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k1"));
        Assert.Contains("already published, or waiting", (await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k2")))).Message);

        // After a failure or a cancellation it can be asked for again.
        await Service().CancelAsync(_a, first.Id);
        await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k3"));
    }

    [Fact]
    public async Task The_same_click_twice_is_one_publication_and_one_queued_message()
    {
        var setup = await Ready();

        var first = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "same"));
        var second = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "same", title: "Different title"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Title, second.Title); // the original, not a changed copy
        Assert.Single(await _db.Publications.ToListAsync());
        Assert.Single(_queue.Items);
    }

    [Fact]
    public async Task The_platform_wide_daily_limit_protects_youtubes_quota_and_a_failed_attempt_does_not_use_it_up()
    {
        _options.Value.MaxPublishesPerDay = 1;
        var one = await Ready();
        var other = await Ready(_b);
        var first = await Service().PublishAsync(_a, one.Version.Id, Request(one));

        _tenant.TenantId = _b;
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Service().PublishAsync(_b, other.Version.Id, Request(other)));
        Assert.Contains("allowance is used up", ex.Message);

        _tenant.TenantId = _a;
        (await _db.Publications.SingleAsync(p => p.Id == first.Id)).Status = PublicationStatus.Failed;
        await _db.SaveChangesAsync();
        _tenant.TenantId = _b;
        await Service().PublishAsync(_b, other.Version.Id, Request(other, "second"));
    }

    // ----- cancel and retry ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_publication_that_has_not_started_can_be_cancelled()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));

        var stored = await _db.Publications.SingleAsync();
        stored.Status = PublicationStatus.Uploading;
        await _db.SaveChangesAsync();
        Assert.Contains("being sent right now", (await Assert.ThrowsAsync<ConflictException>(() => Service().CancelAsync(_a, dto.Id))).Message);

        stored.Status = PublicationStatus.Published;
        await _db.SaveChangesAsync();
        Assert.Contains("already finished", (await Assert.ThrowsAsync<ConflictException>(() => Service().CancelAsync(_a, dto.Id))).Message);

        stored.Status = PublicationStatus.Scheduled;
        await _db.SaveChangesAsync();
        Assert.Equal("Cancelled", (await Service().CancelAsync(_a, dto.Id)).Status);
        await Processor().RunAsync(dto.Id);
        Assert.Empty(_youtube.Uploads);
    }

    [Fact]
    public async Task A_failed_publication_can_be_tried_again_unless_youtube_already_has_the_video_or_the_gate_no_longer_passes()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));
        await Assert.ThrowsAsync<ConflictException>(() => Service().RetryAsync(_a, dto.Id)); // not failed

        var stored = await _db.Publications.SingleAsync();
        stored.Status = PublicationStatus.Failed;
        stored.Attempts = 4;
        stored.LastError = "boom";
        await _db.SaveChangesAsync();
        _queue.Items.Clear();

        var again = await Service().RetryAsync(_a, dto.Id);
        Assert.Equal(("Scheduled", 0, null), (again.Status, again.Attempts, again.LastError));
        Assert.Equal((dto.Id, (TimeSpan?)null), Assert.Single(_queue.Items));

        stored.Status = PublicationStatus.Failed;
        stored.ExternalPostId = "vid9";
        await _db.SaveChangesAsync();
        Assert.Contains("already has this video", (await Assert.ThrowsAsync<ConflictException>(() => Service().RetryAsync(_a, dto.Id))).Message);

        stored.ExternalPostId = null;
        (await _db.ContentScenes.FirstAsync()).Narration = "Changed.";
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => Service().RetryAsync(_a, dto.Id));
    }

    // ----- sending ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_publication_is_sent_with_the_title_privacy_and_disclosure_and_ends_published_with_the_link()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, privacy: "unlisted"));

        await Processor().RunAsync(dto.Id);

        var row = Fresh(dto.Id);
        Assert.Equal((PublicationStatus.Published, "vid1", "https://www.youtube.com/watch?v=vid1", 1), (row.Status, row.ExternalPostId, row.Url, row.Attempts));
        Assert.Equal(_clock.Now, row.PublishedAtUtc);
        var upload = Assert.Single(_youtube.Uploads);
        Assert.Equal(("access-1", "Cataract surgery explained", "unlisted", true), (upload.Token, upload.Request.Title, upload.Request.Privacy, upload.Request.SyntheticMedia));
        Assert.Equal((2048L, 2048L), (upload.Length, upload.Read)); // the whole video, streamed
        Assert.Empty(_youtube.Refreshes);
        Assert.Equal(_clock.Now, (await _db.SocialConnections.SingleAsync()).LastUsedAtUtc);
    }

    [Fact]
    public async Task A_token_about_to_run_out_is_renewed_first_and_the_new_one_is_stored_encrypted()
    {
        var setup = await Ready();
        (await _db.SocialConnections.SingleAsync()).TokenExpiresAtUtc = _clock.Now.AddSeconds(30);
        await _db.SaveChangesAsync();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));

        await Processor().RunAsync(dto.Id);

        Assert.Equal(new[] { "refresh-1" }, _youtube.Refreshes);
        Assert.Equal("access-2", _youtube.Uploads.Single().Token);
        var connection = await _db.SocialConnections.SingleAsync();
        Assert.Equal(("enc:access-2", "enc:refresh-1", _clock.Now.AddHours(2)), (connection.EncryptedAccessToken, connection.EncryptedRefreshToken, connection.TokenExpiresAtUtc));
    }

    [Fact]
    public async Task A_sign_in_google_no_longer_accepts_marks_the_account_for_reconnecting_and_sends_nothing()
    {
        var setup = await Ready();
        (await _db.SocialConnections.SingleAsync()).TokenExpiresAtUtc = _clock.Now.AddSeconds(30);
        await _db.SaveChangesAsync();
        _youtube.RefreshFails = new SocialProviderException("Google no longer accepts the saved sign-in. Connect your YouTube account again.", SocialFailure.NeedsReconnect);
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));

        await Processor().RunAsync(dto.Id);

        var row = Fresh(dto.Id);
        Assert.Equal(PublicationStatus.Failed, row.Status);
        Assert.Contains("Connect your YouTube account again", row.LastError);
        var connection = await _db.SocialConnections.SingleAsync();
        Assert.Equal(SocialConnectionStatus.NeedsReconnect, connection.Status);
        Assert.NotNull(connection.LastError);
        Assert.Empty(_youtube.Uploads);
        Assert.Empty(_queue.Items.Skip(1)); // no retry queued
    }

    [Fact]
    public async Task A_temporary_problem_is_retried_with_growing_waits_and_given_up_after_four_tries()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));
        for (var i = 0; i < PublishingLimits.MaxAttempts; i++)
            _youtube.UploadOutcomes.Enqueue(new SocialProviderException("Google could not be reached. This is usually temporary.", SocialFailure.Transient));
        _queue.Items.Clear();

        var waits = new List<TimeSpan?>();
        for (var attempt = 1; attempt <= PublishingLimits.MaxAttempts; attempt++)
        {
            await Processor().RunAsync(dto.Id);
            var row = Fresh(dto.Id);
            Assert.Equal(attempt, row.Attempts);
            if (attempt < PublishingLimits.MaxAttempts)
            {
                Assert.Equal(PublicationStatus.Scheduled, row.Status);
                Assert.Contains("Trying again automatically", row.LastError);
                Assert.Equal(_clock.Now + _queue.Items.Last().Delay, row.NextRetryAtUtc);
                waits.Add(_queue.Items.Last().Delay);
            }
        }

        Assert.Equal(new TimeSpan?[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30) }, waits);
        var final = Fresh(dto.Id);
        Assert.Equal(PublicationStatus.Failed, final.Status);
        Assert.Contains("tried 4 times", final.LastError);
        Assert.Null(final.NextRetryAtUtc);
        Assert.Equal(3, _queue.Items.Count);
    }

    [Fact]
    public async Task A_refusal_that_will_not_change_fails_at_once_with_the_reason_and_is_not_retried()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));
        _youtube.UploadOutcomes.Enqueue(new SocialProviderException("YouTube's daily allowance for uploads from this platform is used up. Try again tomorrow.", SocialFailure.Permanent));
        _queue.Items.Clear();

        await Processor().RunAsync(dto.Id);

        var row = Fresh(dto.Id);
        Assert.Equal((PublicationStatus.Failed, 1), (row.Status, row.Attempts));
        Assert.Contains("allowance", row.LastError);
        Assert.Empty(_queue.Items);
    }

    [Fact]
    public async Task A_connection_lost_while_the_video_was_being_sent_is_never_retried_by_itself_because_it_may_already_be_there()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));
        _youtube.UploadOutcomes.Enqueue(new SocialProviderException("Contact was lost while the video was being sent, so it may or may not be on YouTube. Look at your channel before trying again.", SocialFailure.Ambiguous));
        _queue.Items.Clear();

        await Processor().RunAsync(dto.Id);

        var row = Fresh(dto.Id);
        Assert.Equal(PublicationStatus.Failed, row.Status);
        Assert.Contains("Look at your channel", row.LastError);
        Assert.Empty(_queue.Items);

        // A raw network error from anywhere else in the sending is treated the same way.
        await Service().RetryAsync(_a, dto.Id);
        _youtube.UploadOutcomes.Enqueue(new HttpRequestException("reset"));
        await Processor().RunAsync(dto.Id);
        Assert.Contains("may or may not be on YouTube", Fresh(dto.Id).LastError);
        Assert.Equal(PublicationStatus.Failed, Fresh(dto.Id).Status);
    }

    [Fact]
    public async Task A_video_youtube_already_has_is_never_sent_again_and_a_finished_cancelled_or_running_publication_is_left_alone()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));

        (await _db.Publications.SingleAsync()).ExternalPostId = "vidExisting"; // its result had not been saved the first time
        await _db.SaveChangesAsync();
        await Processor().RunAsync(dto.Id);
        Assert.Equal((PublicationStatus.Published, "vidExisting"), (Fresh(dto.Id).Status, Fresh(dto.Id).ExternalPostId));
        Assert.Empty(_youtube.Uploads);

        await Processor().RunAsync(dto.Id); // asked again after it finished
        Assert.Empty(_youtube.Uploads);

        foreach (var status in new[] { PublicationStatus.Cancelled, PublicationStatus.Uploading, PublicationStatus.Failed })
        {
            (await _db.Publications.SingleAsync()).Status = status;
            await _db.SaveChangesAsync();
            await Processor().RunAsync(dto.Id);
            Assert.Equal(status, Fresh(dto.Id).Status);
        }

        Assert.Empty(_youtube.Uploads);
    }

    [Fact]
    public async Task Woken_before_its_time_a_publication_waits_for_the_real_time()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, at: Start.AddHours(2)));
        _queue.Items.Clear();
        _clock.Now = Start.AddHours(1);

        await Processor().RunAsync(dto.Id);

        Assert.Empty(_youtube.Uploads);
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(_queue.Items).Delay);
        Assert.Equal(PublicationStatus.Scheduled, Fresh(dto.Id).Status);

        _clock.Now = Start.AddHours(2);
        await Processor().RunAsync(dto.Id);
        Assert.Equal(PublicationStatus.Published, Fresh(dto.Id).Status);
    }

    [Fact]
    public async Task The_gate_runs_again_just_before_sending_so_a_script_changed_or_an_account_removed_in_between_stops_it()
    {
        var setup = await Ready();
        var changed = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k1"));
        (await _db.ContentScenes.FirstAsync()).Narration = "Changed after the request.";
        await _db.SaveChangesAsync();

        await Processor().RunAsync(changed.Id);

        Assert.Equal(PublicationStatus.Failed, Fresh(changed.Id).Status);
        Assert.Contains("no longer matches what was approved", Fresh(changed.Id).LastError);

        (await _db.ContentScenes.FirstAsync()).Narration = "Scene 1.";
        await _db.SaveChangesAsync();
        var removed = await Service().PublishAsync(_a, setup.Version.Id, Request(setup, "k2"));
        _db.SocialConnections.Remove(await _db.SocialConnections.SingleAsync());
        await _db.SaveChangesAsync();
        await Processor().RunAsync(removed.Id);
        Assert.Contains("disconnected", Fresh(removed.Id).LastError);
        Assert.Empty(_youtube.Uploads);
    }

    [Fact]
    public async Task A_video_file_outside_the_tenants_folder_is_never_opened()
    {
        var setup = await Ready();
        var dto = await Service().PublishAsync(_a, setup.Version.Id, Request(setup));
        (await _db.RenderJobs.SingleAsync()).OutputStorageKey = $"s3:private/{_b:N}/product-knowledge/studio/renders/theirs.mp4";
        await _db.SaveChangesAsync();

        await Processor().RunAsync(dto.Id);

        Assert.Equal(PublicationStatus.Failed, Fresh(dto.Id).Status);
        Assert.Empty(_youtube.Uploads);
    }

    // ----- isolation -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_accounts_scripts_and_publications_are_never_seen_used_or_changed()
    {
        var mine = await Ready();
        var dto = await Service().PublishAsync(_a, mine.Version.Id, Request(mine));
        _tenant.TenantId = _b;

        Assert.Empty(await Service().ListConnectionsAsync(_b));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().DisconnectAsync(_b, mine.Connection.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().PublishAsync(_b, mine.Version.Id, Request(mine, "x")));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().ListAsync(_b, mine.Version.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().CancelAsync(_b, dto.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Service().RetryAsync(_b, dto.Id));
        _tenant.TenantId = _a;

        Assert.Equal(PublicationStatus.Scheduled, Fresh(dto.Id).Status);
        Assert.Single(await Service().ListConnectionsAsync(_a));
        Assert.Single(await Service().ListAsync(_a, mine.Version.Id));
    }

    [Fact]
    public void The_endpoints_are_admin_only_need_the_studio_switched_on_and_the_secrets_are_platform_settings()
    {
        var controller = typeof(WhatsAppSalesAutomation.Api.Controllers.ContentStudioPublishingController);
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(controller, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.Equal(WhatsAppSalesAutomation.Domain.Constants.AppRoles.Admin, authorize.Roles);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(WhatsAppSalesAutomation.Api.Filters.RequiresFeatureAttribute)));
        Assert.DoesNotContain(controller.GetMethods(), m => Attribute.IsDefined(m, typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)));

        var all = WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All;
        Assert.True(all.Single(d => d.Key == "ContentStudio:Social:GoogleClientSecret").IsSecret);
        Assert.False(all.Single(d => d.Key == "ContentStudio:Social:GoogleClientId").IsSecret);
        Assert.Equal("5", all.Single(d => d.Key == "ContentStudio:Social:MaxPublishesPerDay").DefaultValue);
    }
}
