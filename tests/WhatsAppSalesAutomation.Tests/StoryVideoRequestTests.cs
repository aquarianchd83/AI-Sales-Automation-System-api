using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Ai.Tasks;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// A tenant raises a request for a video; the platform admin writes the script (charged to the tenant) and attaches the finished video, which is stored
/// under the tenant's folder, recorded, and announced to the tenant in-app.
/// </summary>
public sealed class StoryVideoRequestTests : IDisposable
{
    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class PlatformContext : ITenantContext
    {
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => true;
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

    private sealed class Metering : IStudioMeteringService
    {
        public List<StudioChargeRequest> Charges { get; } = new();
        public List<string> Log { get; } = new();
        public List<Guid> ChargedIds { get; } = new();
        public List<(Guid Id, string Reason)> Refunds { get; } = new();
        public List<Guid> Completed { get; } = new();
        public bool Allow { get; set; } = true;

        public Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
        {
            if (!Allow)
                return Task.FromResult(new StudioChargeResult(false, Guid.NewGuid(), false, "You don't have enough credits left for this. Buy credits to continue."));
            Charges.Add(request);
            Log.Add("charge");
            var id = Guid.NewGuid();
            ChargedIds.Add(id);
            return Task.FromResult(new StudioChargeResult(true, id, false, null));
        }

        public Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default) { Log.Add("complete"); Completed.Add(activityId); return Task.CompletedTask; }

        public Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default) { Log.Add("refund"); Refunds.Add((activityId, reason)); return Task.CompletedTask; }

        public Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Ai : IAiTaskRunner
    {
        public AiExecutionMode Mode { get; set; } = AiExecutionMode.Simulator;

        public Task<AiTaskResult> RunAsync(AiTaskRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiTaskResult(
                true, null, Mode, "Simulator", null, false, null, null, Guid.NewGuid(), Array.Empty<AiAttempt>(), null, null, null));
    }

    private sealed class DocStore : ITenantDocumentStore
    {
        public bool IsConfigured => true;
        public Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(relativePath);
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string FolderOf(Guid tenantId) => tenantId.ToString();
    }

    /// <summary>Like the bucket: puts a file under the folder of the tenant the context is acting as.</summary>
    private sealed class Bucket : IMediaStorageService
    {
        private readonly ITenantContext _tenant;
        public Bucket(ITenantContext tenant) => _tenant = tenant;
        public List<string> Stored { get; } = new();
        public List<string> Deleted { get; } = new();

        public Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            var key = $"s3:media/{_tenant.TenantId?.ToString("N") ?? "shared"}/{Stored.Count + 1}{Path.GetExtension(fileName)}";
            Stored.Add(key);
            return Task.FromResult(new MediaStorageResult(key, GetPublicUrl(key)));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) { Deleted.Add(storageKey); return Task.CompletedTask; }
        public HashSet<string> Missing { get; } = new();
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Missing.Contains(storageKey) ? throw new FileNotFoundException() : Task.FromResult<Stream>(new MemoryStream(new byte[] { 9, 8, 7 }));
        public string GetPublicUrl(string storageKey) => "https://bucket.example/" + storageKey;
        public string GetLocalPath(string storageKey) => GetPublicUrl(storageKey);
        public bool IsPublicUrl(string url) => true;
    }

    private sealed class Notifier : ITenantNotifier
    {
        public List<TenantNotificationRequest> Sent { get; } = new();

        public Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(true);
        }
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Metering _metering = new();
    private readonly Notifier _notifier = new();
    private readonly Bucket _bucket;
    private readonly Clock _clock = new();
    private readonly StoryVideoRequestService _requests;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public StoryVideoRequestTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        var context = new PlatformContext();
        _db = new SqliteApplicationDbContext(options, context, new NoUser());
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "Bansal Corp", Slug = "bansal" });
        _db.Tenants.Add(new Tenant { Id = _other, Name = "Other Co", Slug = "other" });
        _db.SaveChanges();

        var config = Fake.Of<ITenantConfigOverrideProvider>((m, args) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(new StudioOptions { Enabled = true })
                : throw new NotImplementedException(m.Name));
        var ai = new Ai();
        var knowledge = new KnowledgeService(_db, config, _clock);
        var grounding = new StoryGroundingService(knowledge, knowledge, ai, _metering);
        var studio = new StoryStudioService(_db, config, ai, _metering, new DocStore(),
            new SceneVideoProviderFactory(new ISceneVideoProvider[] { new FlowImportSceneVideoProvider() }), _clock, null, grounding);
        _bucket = new Bucket(context);
        _requests = new StoryVideoRequestService(_db, studio, _bucket, context, _notifier, _clock, _metering);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Fact(Guid tenant, string statement)
    {
        _db.BusinessFacts.Add(new BusinessFact { TenantId = tenant, Category = "Services", Statement = statement, Status = BusinessFactStatus.Verified });
        _db.SaveChanges();
    }

    private static CreateStoryProjectRequest Brief(string title = "Question hook") =>
        new(title, "Sector: Real estate\nProblem: Finding customers is slow", "Hinglish", null, "Small business owners", "Book a free demo", 30,
            SceneVideoProviderKind.Simulator, null, Array.Empty<Guid>());

    private static MemoryStream Video() => new(new byte[] { 1, 2, 3, 4 });

    [Fact]
    public async Task Raising_a_request_takes_the_video_price_first_and_makes_the_story_and_the_request()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        Assert.Equal(StoryVideoRequestStatus.Raised, raised.Status);
        Assert.Equal("Question hook", raised.Title);
        Assert.Null(raised.VideoUrl);
        var charge = Assert.Single(_metering.Charges);
        Assert.Equal(StudioActivityKind.VideoRender, charge.Kind);   // priced as a video (the platform's video render credit cost), not a script step
        Assert.Equal(_tenant, charge.TenantId);
        var project = await _db.StoryProjects.IgnoreQueryFilters().SingleAsync(p => p.Id == raised.ProjectId);
        Assert.Equal(_tenant, project.TenantId);
        Assert.Equal(StoryProjectStatus.Draft, project.Status);
        Assert.Equal("Book a free demo", project.Moral);
        var row = await _db.StoryVideoRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_metering.ChargedIds.Single(), row.ChargeActivityId);   // remembered, so declining can give it back
        Assert.Equal(raised.Id.ToString(), charge.ReferenceId);
    }

    [Fact]
    public async Task A_tenant_who_cannot_pay_gets_a_payment_error_and_nothing_is_created()
    {
        _metering.Allow = false;

        var ex = await Assert.ThrowsAsync<PlanLimitExceededException>(() => _requests.RaiseAsync(_tenant, null, Brief()));

        Assert.Contains("enough credits", ex.Message);
        Assert.Empty(await _db.StoryVideoRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await _db.StoryProjects.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task When_the_story_cannot_be_made_after_the_charge_the_charge_is_given_back()
    {
        var tooShort = new CreateStoryProjectRequest("Hook", "Topic", "Hinglish", null, null, null, 2, SceneVideoProviderKind.Simulator, null, Array.Empty<Guid>());

        await Assert.ThrowsAsync<ArgumentException>(() => _requests.RaiseAsync(_tenant, null, tooShort));

        Assert.Equal(new[] { "charge", "refund" }, _metering.Log);
        Assert.Empty(await _db.StoryVideoRequests.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_requests()
    {
        await _requests.RaiseAsync(_tenant, null, Brief("Mine"));
        await _requests.RaiseAsync(_other, null, Brief("Theirs"));

        var mine = await _requests.ListForTenantAsync(_tenant);

        Assert.Equal("Mine", Assert.Single(mine).Title);
    }

    [Fact]
    public async Task The_platform_admin_sees_every_tenants_requests_and_can_filter_them()
    {
        var a = await _requests.RaiseAsync(_tenant, null, Brief("Alpha"));
        await _requests.RaiseAsync(_other, null, Brief("Beta"));
        await _requests.SaveScriptTextAsync(a.Id, _admin, "Scene 1: the owner searches for leads.");

        var all = await _requests.ListAllAsync(new StoryVideoRequestQuery());
        var waiting = await _requests.ListAllAsync(new StoryVideoRequestQuery { Status = StoryVideoRequestStatus.Raised });
        var byTenant = await _requests.ListAllAsync(new StoryVideoRequestQuery { Search = "Bansal" });

        Assert.Equal(2, all.TotalCount);
        Assert.Contains(all.Items, r => r.TenantName == "Other Co");
        Assert.Equal("Beta", Assert.Single(waiting.Items).Title);
        Assert.Equal("Alpha", Assert.Single(byTenant.Items).Title);
    }

    [Fact]
    public async Task The_admin_writes_the_script_as_plain_text_without_charging_again()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        var detail = await _requests.SaveScriptTextAsync(raised.Id, _admin, "  Scene 1: Still searching for leads?\nScene 2: Not any more.  ");

        Assert.Equal(StoryVideoRequestStatus.ScriptReady, detail.Status);
        Assert.Equal("Scene 1: Still searching for leads?\nScene 2: Not any more.", detail.ScriptText);
        Assert.NotNull(detail.ScriptGeneratedAtUtc);
        Assert.Equal(new[] { "charge" }, _metering.Log);       // only the video price taken when it was raised: the script costs the tenant nothing more
        Assert.Equal("Bansal Corp", detail.TenantName);
        Assert.Equal("Book a free demo", detail.CallToAction);
        Assert.False(detail.CanGenerateVideo);
    }

    [Fact]
    public async Task The_script_can_be_changed_until_the_video_is_attached()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await _requests.SaveScriptTextAsync(raised.Id, _admin, "First draft.");

        var again = await _requests.SaveScriptTextAsync(raised.Id, _admin, "Second draft.");

        Assert.Equal("Second draft.", again.ScriptText);
        Assert.Equal(StoryVideoRequestStatus.ScriptReady, again.Status);

        await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "promo.mp4", "video/mp4", 4);
        await Assert.ThrowsAsync<ConflictException>(() => _requests.SaveScriptTextAsync(raised.Id, _admin, "Too late."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task An_empty_script_is_refused_and_nothing_changes(string text)
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        await Assert.ThrowsAsync<ArgumentException>(() => _requests.SaveScriptTextAsync(raised.Id, _admin, text));

        var detail = await _requests.GetAsync(raised.Id);
        Assert.Equal(StoryVideoRequestStatus.Raised, detail.Status);
        Assert.Null(detail.ScriptText);
    }

    [Fact]
    public async Task A_script_longer_than_the_limit_is_refused()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        await Assert.ThrowsAsync<ArgumentException>(() => _requests.SaveScriptTextAsync(raised.Id, _admin, new string('x', StoryVideoRequestService.MaxScriptChars + 1)));
        await _requests.SaveScriptTextAsync(raised.Id, _admin, new string('x', StoryVideoRequestService.MaxScriptChars));
    }

    [Fact]
    public async Task The_tenant_opens_its_own_request_with_the_script_the_admin_wrote_and_nobody_elses()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await _requests.SaveScriptTextAsync(raised.Id, _admin, "Scene 1: hello.");

        var mine = await _requests.GetForTenantAsync(_tenant, raised.Id);

        Assert.Equal("Question hook", mine.Title);
        Assert.Equal(StoryVideoRequestStatus.ScriptReady, mine.Status);
        Assert.Equal("Scene 1: hello.", mine.ScriptText);
        Assert.Equal("Book a free demo", mine.CallToAction);
        Assert.Contains("Sector: Real estate", mine.Topic);
        await Assert.ThrowsAsync<NotFoundException>(() => _requests.GetForTenantAsync(_other, raised.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _requests.GetForTenantAsync(_tenant, Guid.NewGuid()));
    }

    [Fact]
    public async Task An_unknown_request_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _requests.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_uploaded_video_lands_under_the_tenants_folder_is_recorded_and_the_tenant_is_told_in_app()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        var detail = await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "promo.mp4", "video/mp4", 4);

        var key = Assert.Single(_bucket.Stored);
        Assert.Contains(_tenant.ToString("N"), key);      // the tenant's folder, not "shared"
        Assert.Equal(StoryVideoRequestStatus.VideoReady, detail.Status);
        Assert.Equal("https://bucket.example/" + key, detail.VideoUrl);
        Assert.Equal("promo.mp4", detail.VideoFileName);
        Assert.Equal("Uploaded", detail.VideoSource);

        var row = await _db.StoryVideoRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(key, row.VideoStorageKey);
        Assert.Equal(_admin, row.VideoAttachedBy);

        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal(_tenant, notice.TenantId);
        Assert.Equal(TenantNotificationKind.StoryVideoReady, notice.Kind);
        Assert.Contains("Question hook", notice.Body);
        Assert.False(notice.AlsoWhatsApp);

        Assert.Equal("https://bucket.example/" + key, Assert.Single(await _requests.ListForTenantAsync(_tenant)).VideoUrl);
    }

    [Fact]
    public async Task Replacing_the_video_removes_the_old_file_and_tells_the_tenant_again()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "first.mp4", "video/mp4", 4);
        var first = _bucket.Stored.Single();
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "second.webm", "video/webm", 4);

        Assert.Equal(new[] { first }, _bucket.Deleted);
        Assert.Equal(2, _notifier.Sent.Count);
        Assert.NotEqual(_notifier.Sent[0].EpisodeKey, _notifier.Sent[1].EpisodeKey);
    }

    [Theory]
    [InlineData("notes.pdf", "application/pdf", 10)]
    [InlineData("promo.mp4", "text/plain", 10)]
    [InlineData("promo.exe", "video/mp4", 10)]
    [InlineData("promo.mp4", "video/mp4", 0)]
    [InlineData("promo.mp4", "video/mp4", StoryVideoRequestService.MaxVideoBytes + 1)]
    public async Task Only_a_video_within_the_size_limit_is_accepted_and_nothing_is_stored_or_sent_otherwise(string name, string type, long length)
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        await Assert.ThrowsAsync<ArgumentException>(() => _requests.AttachVideoAsync(raised.Id, _admin, Video(), name, type, length));

        Assert.Empty(_bucket.Stored);
        Assert.Empty(_notifier.Sent);
        Assert.Equal(StoryVideoRequestStatus.Raised, (await _requests.GetAsync(raised.Id)).Status);
    }

    [Fact]
    public void The_status_goes_to_the_portal_as_a_word()
    {
        var json = JsonSerializer.Serialize(new StoryVideoRequestDto(Guid.NewGuid(), Guid.NewGuid(), "t", StoryVideoRequestStatus.ScriptReady, DateTime.UtcNow, null, null, null, null, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"ScriptReady\"", json);
    }

    [Fact]
    public async Task Delivering_the_video_completes_the_charge()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "promo.mp4", "video/mp4", 4);

        Assert.Equal(_metering.ChargedIds, _metering.Completed);
        Assert.Empty(_metering.Refunds);
    }

    [Fact]
    public async Task Declining_returns_the_credits_and_tells_the_tenant_why()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());

        var detail = await _requests.RejectAsync(raised.Id, _admin, "  The video engine could not make this one.  ");

        Assert.Equal(StoryVideoRequestStatus.Rejected, detail.Status);
        Assert.Equal("The video engine could not make this one.", detail.RejectedReason);
        var refund = Assert.Single(_metering.Refunds);
        Assert.Equal(_metering.ChargedIds.Single(), refund.Id);
        Assert.Contains("declined", refund.Reason);
        var notice = Assert.Single(_notifier.Sent);
        Assert.Equal(TenantNotificationKind.StoryVideoRejected, notice.Kind);
        Assert.Equal(_tenant, notice.TenantId);
        Assert.Contains("could not make this one", notice.Body);
        Assert.Contains("refunded", notice.Body);
        Assert.Equal("Your credits were refunded", notice.Title);
        Assert.Equal("The video engine could not make this one.", Assert.Single(await _requests.ListForTenantAsync(_tenant)).RejectedReason);
    }

    [Fact]
    public async Task A_reason_is_required_to_decline_and_a_delivered_or_declined_request_cannot_be_declined()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await Assert.ThrowsAsync<ArgumentException>(() => _requests.RejectAsync(raised.Id, _admin, "  "));
        Assert.Empty(_metering.Refunds);

        await _requests.RejectAsync(raised.Id, _admin, "Cannot be made.");
        await Assert.ThrowsAsync<ConflictException>(() => _requests.RejectAsync(raised.Id, _admin, "Again."));

        var delivered = await _requests.RaiseAsync(_tenant, null, Brief("Second"));
        await _requests.AttachVideoAsync(delivered.Id, _admin, Video(), "promo.mp4", "video/mp4", 4);
        await Assert.ThrowsAsync<ConflictException>(() => _requests.RejectAsync(delivered.Id, _admin, "Too late."));
        Assert.Single(_metering.Refunds);
    }

    [Fact]
    public async Task A_declined_request_gets_no_script_and_no_video()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await _requests.RejectAsync(raised.Id, _admin, "Cannot be made.");

        await Assert.ThrowsAsync<ConflictException>(() => _requests.SaveScriptTextAsync(raised.Id, _admin, "A script."));
        await Assert.ThrowsAsync<ConflictException>(() => _requests.AttachVideoAsync(raised.Id, _admin, Video(), "promo.mp4", "video/mp4", 4));

        Assert.Empty(_bucket.Stored);
    }

    [Fact]
    public void Declined_requests_go_to_the_portal_as_a_word()
    {
        var json = JsonSerializer.Serialize(new StoryVideoRequestDto(Guid.NewGuid(), Guid.NewGuid(), "t", StoryVideoRequestStatus.Rejected, DateTime.UtcNow, null, null, null, "why", null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"Rejected\"", json);
    }

    /// <summary>The fake metering writes no activity rows, so stand one in for the charge it reported.</summary>
    private void RecordCharge(decimal units, decimal refunded = 0)
    {
        _db.StudioActivities.Add(new StudioActivity
        {
            Id = _metering.ChargedIds.Last(), TenantId = _tenant, Kind = StudioActivityKind.VideoRender, QuotaType = QuotaType.VideoRenders,
            Description = "Video requested in the Story studio", OperationKey = "k" + Guid.NewGuid().ToString("N"), Units = units, UnitsRefunded = refunded,
            Status = StudioActivityStatus.Charged, RequestedAtUtc = _clock.UtcNow
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task The_platform_list_and_the_tenants_own_list_show_the_prepaid_credits_and_what_was_refunded()
    {
        var a = await _requests.RaiseAsync(_tenant, null, Brief("Alpha"));
        RecordCharge(1500);
        var b = await _requests.RaiseAsync(_tenant, null, Brief("Beta"));
        RecordCharge(1500, refunded: 1500);

        var all = await _requests.ListAllAsync(new StoryVideoRequestQuery());
        var mine = await _requests.ListForTenantAsync(_tenant);

        var alpha = all.Items.Single(r => r.Title == "Alpha");
        Assert.Equal(1500m, alpha.PrepaidCredits);
        Assert.Equal(0m, alpha.CreditsRefunded);
        var beta = all.Items.Single(r => r.Title == "Beta");
        Assert.Equal(1500m, beta.PrepaidCredits);
        Assert.Equal(1500m, beta.CreditsRefunded);
        Assert.Equal(1500m, mine.Single(r => r.Id == a.Id).PrepaidCredits);
        Assert.Equal(1500m, mine.Single(r => r.Id == b.Id).CreditsRefunded);

        var detail = await _requests.GetAsync(a.Id);
        Assert.Equal(1500m, detail.PrepaidCredits);
        Assert.Equal(0m, detail.CreditsRefunded);
    }

    [Fact]
    public async Task A_tenant_can_download_its_own_finished_video_under_its_file_name()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "promo.webm", "video/webm", 4);

        var video = await _requests.OpenVideoAsync(_tenant, raised.Id);

        Assert.Equal("promo.webm", video.FileName);
        Assert.Equal("video/webm", video.ContentType);
        using var buffer = new MemoryStream();
        await video.Content.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 9, 8, 7 }, buffer.ToArray());
    }

    [Fact]
    public async Task Another_tenants_request_cannot_be_downloaded_and_a_video_that_is_not_ready_is_a_conflict()
    {
        var mine = await _requests.RaiseAsync(_tenant, null, Brief());

        await Assert.ThrowsAsync<ConflictException>(() => _requests.OpenVideoAsync(_tenant, mine.Id));
        await _requests.AttachVideoAsync(mine.Id, _admin, Video(), "promo.mp4", "video/mp4", 4);
        await Assert.ThrowsAsync<NotFoundException>(() => _requests.OpenVideoAsync(_other, mine.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _requests.OpenVideoAsync(_tenant, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_video_that_is_gone_from_the_bucket_says_so_plainly()
    {
        var raised = await _requests.RaiseAsync(_tenant, null, Brief());
        await _requests.AttachVideoAsync(raised.Id, _admin, Video(), "promo.mp4", "video/mp4", 4);
        _bucket.Missing.Add(_bucket.Stored.Single());

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => _requests.OpenVideoAsync(_tenant, raised.Id));

        Assert.Contains("no longer available", ex.Message);
    }
}
