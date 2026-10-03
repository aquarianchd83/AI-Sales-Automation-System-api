using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The platform's own WhatsApp: a template per kind of notice, seeded and then owned by the admin, sent from the platform's number only once
/// Meta has approved it - plus the media library its images come from and the settings page that holds the number's credentials.</summary>
public sealed class PlatformWhatsAppTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly FakeTemplateAdmin _admin = new();
    private readonly FakePlatformWhatsApp _sender = new();
    private readonly MemoryStorage _storage = new();

    public PlatformWhatsAppTests()
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
        var provider = new TestServices(_db);
        await PlatformTemplateSeeder.SeedAsync(provider);
    }

    private PlatformMessageTemplateService Templates() =>
        new(_db, _admin, _sender, _storage, new UpdatePlatformMessageTemplateRequestValidator());

    private Task<PlatformMessageTemplate> TemplateAsync(TenantNotificationKind kind) =>
        _db.PlatformMessageTemplates.SingleAsync(t => t.EventKey == kind.ToString());

    private static UpdatePlatformMessageTemplateRequest Edit(PlatformMessageTemplate t, string? body = null, bool? active = null, Guid? header = null, bool removeHeader = false) =>
        new(t.Name, body ?? t.BodyText, active ?? t.IsActive, header, removeHeader);

    // ── The catalog and the seeder ───────────────────────────────────────────────────────

    [Fact]
    public void Every_seeded_text_is_acceptable_to_the_editor_and_to_meta()
    {
        var validator = new UpdatePlatformMessageTemplateRequestValidator();

        foreach (var d in PlatformTemplateCatalog.All)
        {
            Assert.True(validator.Validate(new UpdatePlatformMessageTemplateRequest(d.Name, d.Body, true, null, false)).IsValid, d.Name);
            Assert.Empty(PlatformTemplateCatalog.UnknownTokens(d.Body));
            Assert.Matches("^[a-z0-9_]+$", d.WhatsAppTemplateName);

            // The worst case a body can reach: both values at their longest.
            var longest = d.Body.Replace("{{TenantName}}", new string('x', 100)).Replace("{{Message}}", new string('x', PlatformTemplateCatalog.MaxValueLength));
            Assert.True(longest.Length <= PlatformTemplateCatalog.MaxBodyLength, $"{d.Name} could exceed Meta's 1024 characters ({longest.Length})");
        }

        Assert.Equal(PlatformTemplateCatalog.All.Count, PlatformTemplateCatalog.All.Select(d => d.WhatsAppTemplateName).Distinct().Count());
        Assert.Equal(PlatformTemplateCatalog.All.Count, PlatformTemplateCatalog.All.Select(d => d.Kind).Distinct().Count());
    }

    [Fact]
    public void The_notices_a_tenant_can_get_on_whatsapp_all_have_a_template()
    {
        var kinds = PlatformTemplateCatalog.All.Select(d => d.Kind).ToHashSet();

        foreach (var needed in new[]
                 {
                     TenantNotificationKind.QuotaLow20, TenantNotificationKind.QuotaLow5, TenantNotificationKind.QuotaExhausted,
                     TenantNotificationKind.CreditsExpiring14, TenantNotificationKind.CreditsExpiring3,
                     TenantNotificationKind.PlanExpiring7, TenantNotificationKind.PlanExpiring1, TenantNotificationKind.PlanRenewalFailed,
                     TenantNotificationKind.RefundApproved, TenantNotificationKind.RefundRejected, TenantNotificationKind.RefundExpired
                 })
            Assert.Contains(needed, kinds);

        // Nothing security-sensitive is ever pushed to WhatsApp.
        Assert.DoesNotContain(TenantNotificationKind.AccountLocked, kinds);
    }

    [Fact]
    public async Task Seeding_creates_one_pending_template_per_notice_and_is_repeatable()
    {
        await SeedAsync();
        await SeedAsync();

        var all = await _db.PlatformMessageTemplates.ToListAsync();
        Assert.Equal(PlatformTemplateCatalog.All.Count, all.Count);
        Assert.All(all, t =>
        {
            Assert.Equal(WhatsAppTemplateStatus.Pending, t.WhatsAppTemplateStatus);
            Assert.Equal(TemplateCategory.Utility, t.Category);
            Assert.True(t.IsActive);
        });
    }

    [Fact]
    public async Task A_restart_never_overwrites_what_the_admin_changed()
    {
        await SeedAsync();
        var plan = await TemplateAsync(TenantNotificationKind.PlanExpiring7);
        plan.BodyText = "Hello {{TenantName}}, my own wording here. {{Message}} Thanks for being with us.";
        plan.IsActive = false;
        await _db.SaveChangesAsync();

        await SeedAsync();

        var after = await TemplateAsync(TenantNotificationKind.PlanExpiring7);
        Assert.Contains("my own wording", after.BodyText);
        Assert.False(after.IsActive);
    }

    // ── Sending a notice ─────────────────────────────────────────────────────────────────

    private async Task<PlatformNoticeResolution> ResolveAsync(TenantNotificationKind kind) =>
        await new PlatformNoticeTemplates(_db, _storage).ResolveAsync(kind, "Acme Traders", "Plan ends soon", "Your plan ends on 10 Oct.\nRenews for $99.");

    [Fact]
    public async Task An_approved_template_is_filled_with_the_tenant_and_the_message()
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.PlanExpiring7);
        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        await _db.SaveChangesAsync();

        var resolution = await ResolveAsync(TenantNotificationKind.PlanExpiring7);

        Assert.NotNull(resolution.Message);
        Assert.Equal("tenant_alert_plan_expiring_7", resolution.Message!.TemplateName);
        // First-occurrence order of the tokens in the body, with the line break folded: Meta refuses a parameter holding one.
        Assert.Equal(new[] { "Acme Traders", "Your plan ends on 10 Oct. Renews for $99." }, resolution.Message.Parameters);
        Assert.Null(resolution.Message.MediaUrl);
    }

    [Theory]
    [InlineData(WhatsAppTemplateStatus.Pending)]
    [InlineData(WhatsAppTemplateStatus.Rejected)]
    public async Task A_template_meta_has_not_approved_is_skipped_with_the_reason(WhatsAppTemplateStatus status)
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.QuotaExhausted);
        t.WhatsAppTemplateStatus = status;
        await _db.SaveChangesAsync();

        var resolution = await ResolveAsync(TenantNotificationKind.QuotaExhausted);

        Assert.Null(resolution.Message);
        Assert.Contains(status.ToString(), resolution.SkipNote);
    }

    [Fact]
    public async Task A_switched_off_or_missing_template_sends_nothing()
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.QuotaLow5);
        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        t.IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Contains("switched off", (await ResolveAsync(TenantNotificationKind.QuotaLow5)).SkipNote);
        Assert.Contains("no WhatsApp template", (await ResolveAsync(TenantNotificationKind.JobCompleted)).SkipNote);
    }

    [Fact]
    public async Task The_image_is_attached_only_to_a_template_meta_holds_with_an_image_header()
    {
        await SeedAsync();
        var asset = new PlatformMediaAsset { FileName = "hero.png", ContentType = "image/png", SizeBytes = 10, StorageProvider = "Local", StorageKey = "hero.png", Url = "/media/hero.png", Checksum = "abc" };
        _db.PlatformMediaAssets.Add(asset);
        var t = await TemplateAsync(TenantNotificationKind.CreditsAdded);
        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        t.HeaderMediaAssetId = asset.Id;
        t.HeaderOnMeta = false;
        await _db.SaveChangesAsync();

        Assert.Null((await ResolveAsync(TenantNotificationKind.CreditsAdded)).Message!.MediaUrl);

        t.HeaderOnMeta = true;
        await _db.SaveChangesAsync();
        Assert.Equal("https://cdn.example.test/hero.png", (await ResolveAsync(TenantNotificationKind.CreditsAdded)).Message!.MediaUrl);
    }

    [Fact]
    public async Task The_notifier_sends_the_notices_own_template_and_says_why_when_it_cannot()
    {
        await SeedAsync();
        var tenant = new Tenant { Name = "Acme", Slug = "acme", Status = TenantStatus.Active, BillingAlertEmail = "b@acme.test", BillingAlertPhoneE164 = "+919876543210", BillingAlertWhatsAppEnabled = true };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        var notifier = new TenantNotifier(
            _db, null!, new FakeEmail(), _sender, new FakeNotificationBroadcaster(), new FixedOptions<BillingAlertOptions>(new()),
            NullLogger<TenantNotifier>.Instance, new PlatformNoticeTemplates(_db, _storage));

        // Still Pending: nothing goes, and the notification records why.
        await notifier.NotifyAsync(new TenantNotificationRequest(tenant.Id, TenantNotificationKind.PlanExpiring1, null, "e1", "Plan ends tomorrow", "It ends tomorrow.", AlsoWhatsApp: true));
        Assert.Empty(_sender.Sent);
        var first = await _db.TenantNotifications.IgnoreQueryFilters().SingleAsync(n => n.EpisodeKey == "e1");
        Assert.Equal(DeliveryStatus.Skipped, first.WhatsAppStatus);
        Assert.Contains("Pending", first.DeliveryNote);

        var t = await TemplateAsync(TenantNotificationKind.PlanExpiring1);
        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        await _db.SaveChangesAsync();

        await notifier.NotifyAsync(new TenantNotificationRequest(tenant.Id, TenantNotificationKind.PlanExpiring1, null, "e2", "Plan ends tomorrow", "It ends tomorrow.", AlsoWhatsApp: true));
        Assert.Single(_sender.Sent);
        Assert.Equal("tenant_alert_plan_expiring_1", _sender.Templates.Single().Template);
        Assert.Equal(DeliveryStatus.Sent, (await _db.TenantNotifications.IgnoreQueryFilters().SingleAsync(n => n.EpisodeKey == "e2")).WhatsAppStatus);
    }

    // ── Editing, restoring and syncing ───────────────────────────────────────────────────

    [Fact]
    public async Task Rewording_an_approved_template_sends_it_back_to_review_but_a_switch_does_not()
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.RefundApproved);
        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        await _db.SaveChangesAsync();

        await Templates().UpdateAsync(t.Id, Edit(t, active: false));
        Assert.Equal(WhatsAppTemplateStatus.Approved, (await TemplateAsync(TenantNotificationKind.RefundApproved)).WhatsAppTemplateStatus);

        var dto = await Templates().UpdateAsync(t.Id, Edit(t, body: "Hi {{TenantName}}, a different sentence about your refund. {{Message}} Goodbye for now."));
        Assert.Equal("Pending", dto.Status);
    }

    [Theory]
    [InlineData("Hi {{Frist}}, hello there. {{Message}} Bye.")]
    [InlineData("{{TenantName}} your plan ends. {{Message}} Bye.")]
    [InlineData("Hi {{TenantName}}, your plan ends. {{Message}}")]
    public async Task A_body_with_an_unknown_placeholder_or_a_variable_at_either_end_is_refused(string body)
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.RefundApproved);

        await Assert.ThrowsAsync<ValidationException>(() => Templates().UpdateAsync(t.Id, Edit(t, body: body)));
    }

    [Fact]
    public async Task Restoring_the_default_puts_the_seeded_wording_back()
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.QuotaLow20);
        var original = t.BodyText;
        t.BodyText = "Hi {{TenantName}}, edited text that is long enough. {{Message}} End.";
        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        await _db.SaveChangesAsync();

        var dto = await Templates().RestoreDefaultAsync(t.Id);

        Assert.Equal(original, dto.BodyText);
        Assert.Equal("Pending", dto.Status);
    }

    [Fact]
    public async Task Syncing_creates_the_templates_on_meta_then_adopts_the_review_status()
    {
        await SeedAsync();
        _admin.Statuses["tenant_alert_plan_expiring_7"] = "APPROVED";

        var result = await Templates().SyncAsync();

        Assert.True(result.Configured);
        Assert.Equal(PlatformTemplateCatalog.All.Count, result.Created);
        Assert.Empty(result.Failures);
        // Every create carried the Meta-positional body and an example for each variable.
        var plan = _admin.Created.Single(s => s.Name == "tenant_alert_plan_expiring_7");
        Assert.Contains("{{1}}", plan.MetaBodyText);
        Assert.Contains("{{2}}", plan.MetaBodyText);
        Assert.DoesNotContain("TenantName", plan.MetaBodyText);
        Assert.Equal(new[] { "Acme Traders", "Your Growth plan ends on 10 Oct 2026 and renews for USD 99.00." }, plan.ExampleValues);
        Assert.Equal("Utility", plan.Category);

        var after = await TemplateAsync(TenantNotificationKind.PlanExpiring7);
        Assert.NotNull(after.MetaTemplateId);
        Assert.Equal(WhatsAppTemplateStatus.Approved, after.WhatsAppTemplateStatus);
        Assert.Equal(WhatsAppTemplateStatus.Pending, (await TemplateAsync(TenantNotificationKind.QuotaLow5)).WhatsAppTemplateStatus);

        // A second pass has nothing left to push.
        _admin.Created.Clear();
        Assert.Equal(0, (await Templates().SyncAsync()).Created);
        Assert.Empty(_admin.Created);
    }

    [Fact]
    public async Task An_edit_after_the_first_push_is_sent_as_an_update_and_a_rejection_makes_it_unsendable()
    {
        await SeedAsync();
        await Templates().SyncAsync();
        var t = await TemplateAsync(TenantNotificationKind.QuotaExhausted);
        await Templates().UpdateAsync(t.Id, Edit(t, body: "Hi {{TenantName}}, new wording that is long enough. {{Message}} Thank you."));
        _admin.Statuses["tenant_alert_quota_exhausted"] = "REJECTED";

        var result = await Templates().SyncAsync();

        Assert.Equal(1, result.Updated);
        Assert.Single(_admin.Updated);
        Assert.Equal(WhatsAppTemplateStatus.Rejected, (await TemplateAsync(TenantNotificationKind.QuotaExhausted)).WhatsAppTemplateStatus);
    }

    [Fact]
    public async Task A_switched_off_template_is_left_out_of_the_bulk_push_and_a_failure_is_reported_not_thrown()
    {
        await SeedAsync();
        var off = await TemplateAsync(TenantNotificationKind.RefundExpired);
        off.IsActive = false;
        await _db.SaveChangesAsync();
        _admin.FailFor = "tenant_alert_refund_rejected";

        var result = await Templates().SyncAsync();

        Assert.DoesNotContain(_admin.Created, s => s.Name == "tenant_alert_refund_expired");
        Assert.Single(result.Failures);
        Assert.Contains("tenant_alert_refund_rejected", result.Failures[0]);
    }

    [Fact]
    public async Task Without_the_platform_number_nothing_is_attempted()
    {
        await SeedAsync();
        _admin.Configured = false;

        var result = await Templates().SyncAsync();

        Assert.False(result.Configured);
        Assert.Empty(_admin.Created);
        Assert.Null((await TemplateAsync(TenantNotificationKind.QuotaLow5)).MetaTemplateId);
    }

    [Fact]
    public async Task A_test_send_needs_an_approved_template_and_a_proper_number()
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.CreditsExpiring3);

        var notYet = await Templates().SendTestAsync(t.Id, "+919876543210");
        Assert.False(notYet.Success);
        Assert.Contains("Pending", notYet.Message);
        Assert.Empty(_sender.Sent);

        await Assert.ThrowsAsync<ValidationException>(() => Templates().SendTestAsync(t.Id, "9876543210"));

        t.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved;
        await _db.SaveChangesAsync();
        var sent = await Templates().SendTestAsync(t.Id, "+91 98765 43210");
        Assert.True(sent.Success);
        Assert.Equal("+919876543210", _sender.Sent.Single().To);
        Assert.Equal("Acme Traders", _sender.Sent.Single().Parameters[0]);
    }

    // ── Images ───────────────────────────────────────────────────────────────────────────

    private async Task<PlatformMediaAsset> AssetAsync(string name = "hero.png", string type = "image/png", long size = 100)
    {
        var asset = new PlatformMediaAsset { FileName = name, ContentType = type, SizeBytes = size, StorageProvider = "Local", StorageKey = name, Url = "/media/" + name, Checksum = Guid.NewGuid().ToString("N") };
        _db.PlatformMediaAssets.Add(asset);
        await _db.SaveChangesAsync();
        _storage.Files[name] = new byte[] { 1, 2, 3 };
        return asset;
    }

    [Fact]
    public async Task Only_a_small_jpeg_or_png_can_be_a_template_image()
    {
        await SeedAsync();
        var t = await TemplateAsync(TenantNotificationKind.CreditsAdded);

        var video = await AssetAsync("clip.mp4", "video/mp4");
        var huge = await AssetAsync("huge.png", "image/png", 6 * 1024 * 1024);
        var good = await AssetAsync();

        await Assert.ThrowsAsync<ConflictException>(() => Templates().UpdateAsync(t.Id, Edit(t, header: video.Id)));
        await Assert.ThrowsAsync<ConflictException>(() => Templates().UpdateAsync(t.Id, Edit(t, header: huge.Id)));

        var dto = await Templates().UpdateAsync(t.Id, Edit(t, header: good.Id));
        Assert.Equal(good.Id, dto.HeaderMediaAssetId);
        Assert.Equal("hero.png", dto.HeaderFileName);
        Assert.Equal("https://cdn.example.test/hero.png", dto.HeaderUrl);
    }

    [Fact]
    public async Task The_first_push_sends_the_image_to_meta_and_fixes_the_header_there()
    {
        await SeedAsync();
        var good = await AssetAsync();
        var t = await TemplateAsync(TenantNotificationKind.CreditsAdded);
        await Templates().UpdateAsync(t.Id, Edit(t, header: good.Id));

        await Templates().SyncAsync();

        Assert.NotNull(_admin.Created.Single(s => s.Name == "tenant_alert_credits_added").HeaderImage);
        Assert.True((await TemplateAsync(TenantNotificationKind.CreditsAdded)).HeaderOnMeta);

        // Swapping the image is fine; removing it is not - Meta fixed the header at creation.
        var other = await AssetAsync("other.png");
        var t2 = await TemplateAsync(TenantNotificationKind.CreditsAdded);
        await Templates().UpdateAsync(t2.Id, Edit(t2, header: other.Id));
        await Assert.ThrowsAsync<ConflictException>(() => Templates().UpdateAsync(t2.Id, Edit(t2, removeHeader: true)));
    }

    [Fact]
    public async Task An_image_a_template_shows_cannot_be_deleted()
    {
        await SeedAsync();
        var good = await AssetAsync();
        var t = await TemplateAsync(TenantNotificationKind.CreditsAdded);
        await Templates().UpdateAsync(t.Id, Edit(t, header: good.Id));
        var media = new PlatformMediaService(_db, _storage, new FixedOptions<MediaOptions>(new() { MaxSizeBytes = 1000, AllowedContentTypes = new[] { "image/png" } }));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => media.DeleteAsync(good.Id));
        Assert.Contains(t.Name, ex.Message);

        await Templates().UpdateAsync(t.Id, Edit(t, removeHeader: true));
        await media.DeleteAsync(good.Id);
        Assert.Empty(await _db.PlatformMediaAssets.ToListAsync());
    }

    [Fact]
    public async Task The_media_library_checks_type_and_size_and_stores_identical_bytes_once()
    {
        var media = new PlatformMediaService(_db, _storage, new FixedOptions<MediaOptions>(new() { MaxSizeBytes = 10, AllowedContentTypes = new[] { "image/png" } }));

        await Assert.ThrowsAsync<ValidationException>(() => media.UploadAsync(new MemoryStream(new byte[5]), "a.gif", "image/gif", 5, null));
        await Assert.ThrowsAsync<ValidationException>(() => media.UploadAsync(new MemoryStream(new byte[50]), "a.png", "image/png", 50, null));
        await Assert.ThrowsAsync<ValidationException>(() => media.UploadAsync(new MemoryStream(), "a.png", "image/png", 0, null));

        var first = await media.UploadAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "a.png", "image/png", 3, Guid.NewGuid());
        var again = await media.UploadAsync(new MemoryStream(new byte[] { 1, 2, 3 }), "copy.png", "image/png", 3, null);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, _storage.Uploads);
        Assert.Single(await media.GetAllAsync(null));
        Assert.Single(await media.GetAllAsync("a.p"));
        Assert.Empty(await media.GetAllAsync("zzz"));

        var replaced = await media.ReplaceAsync(first.Id, new MemoryStream(new byte[] { 9, 9 }), "b.png", "image/png", 2);
        Assert.Equal(first.Id, replaced.Id);
        Assert.Equal("b.png", replaced.FileName);
    }

    // ── The number's settings ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Saving_the_number_stores_it_masks_the_token_and_keeps_it_when_not_resent()
    {
        var store = new MemoryStore();
        var service = new PlatformWhatsAppSettingsService(store, new UpdatePlatformWhatsAppSettingsRequestValidator(), _sender);

        Assert.False((await service.GetAsync()).IsConfigured);

        var saved = await service.UpdateAsync(new UpdatePlatformWhatsAppSettingsRequest(true, "123456789012345", "987654321098765", "EAAB-secret-token-9876", "v19.0", ""), null);

        Assert.True(saved.IsConfigured);
        Assert.True(saved.CanManageTemplates);
        Assert.Equal("••••9876", saved.AccessTokenHint);
        Assert.Equal("EAAB-secret-token-9876", store.Rows[PlatformWhatsAppSettingsService.AccessTokenKey]);
        Assert.Equal(PlatformWhatsAppSettingsService.DefaultApiBaseUrl, saved.ApiBaseUrl);

        var kept = await service.UpdateAsync(new UpdatePlatformWhatsAppSettingsRequest(true, "123456789012345", "987654321098765", null, "v20.0", ""), null);
        Assert.Equal("EAAB-secret-token-9876", store.Rows[PlatformWhatsAppSettingsService.AccessTokenKey]);
        Assert.Equal("v20.0", kept.ApiVersion);

        var paused = await service.UpdateAsync(new UpdatePlatformWhatsAppSettingsRequest(false, "123456789012345", "987654321098765", null, "v20.0", ""), null);
        Assert.False(paused.IsConfigured);
        Assert.True(paused.HasAccessToken);
    }

    [Theory]
    [InlineData(true, "", "", null)]
    [InlineData(true, "123456789012345", "", "")]
    [InlineData(false, "+919876543210", "", "tok")]
    [InlineData(false, "123456789012345", "not-a-number", "tok")]
    public async Task A_number_that_cannot_work_is_refused(bool enabled, string phoneId, string waba, string? token)
    {
        var service = new PlatformWhatsAppSettingsService(new MemoryStore(), new UpdatePlatformWhatsAppSettingsRequestValidator(), _sender);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(new UpdatePlatformWhatsAppSettingsRequest(enabled, phoneId, waba, token, "v19.0", ""), null));
    }

    [Fact]
    public async Task The_connection_test_sends_metas_sample_template_and_explains_an_unconfigured_number()
    {
        var service = new PlatformWhatsAppSettingsService(new MemoryStore(), new UpdatePlatformWhatsAppSettingsRequestValidator(), _sender);

        var ok = await service.SendTestAsync("+919876543210");
        Assert.True(ok.Success);
        Assert.Equal("hello_world", _sender.Templates.Single().Template);

        _sender.Configured = false;
        var skipped = await service.SendTestAsync("+919876543210");
        Assert.False(skipped.Success);
        Assert.Contains("isn't set up", skipped.Message);

        await Assert.ThrowsAsync<ValidationException>(() => service.SendTestAsync("12345"));
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────

    private sealed class TestServices : IServiceProvider
    {
        private readonly ApplicationDbContext _db;
        public TestServices(ApplicationDbContext db) => _db = db;
        public object? GetService(Type serviceType) => serviceType == typeof(ApplicationDbContext) ? _db : null;
    }

    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    /// <summary>Stands in for Meta: remembers what was created or updated, and answers the review status a test chose.</summary>
    private sealed class FakeTemplateAdmin : IPlatformWhatsAppTemplateAdmin
    {
        public bool Configured { get; set; } = true;
        public Dictionary<string, string> Statuses { get; } = new();
        public string? FailFor { get; set; }
        public List<WhatsAppTemplateSubmission> Created { get; } = new();
        public List<WhatsAppTemplateSubmission> Updated { get; } = new();
        private readonly Dictionary<string, (string Id, string Language, string Category)> _remote = new();

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Configured);

        public Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WhatsAppRemoteTemplate>>(_remote
                .Select(r => new WhatsAppRemoteTemplate(r.Value.Id, r.Key, r.Value.Language, Statuses.GetValueOrDefault(r.Key, "PENDING"), r.Value.Category))
                .ToList());

        public Task<WhatsAppTemplateSubmitResult> CreateTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
        {
            if (submission.Name == FailFor)
                return Task.FromResult(new WhatsAppTemplateSubmitResult(false, null, null, "Meta said no."));

            Created.Add(submission);
            var id = "meta-" + submission.Name;
            _remote[submission.Name] = (id, submission.Language, submission.Category.ToUpperInvariant());
            return Task.FromResult(new WhatsAppTemplateSubmitResult(true, id, "PENDING", null));
        }

        public Task<WhatsAppTemplateSubmitResult> UpdateTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
        {
            Updated.Add(submission);
            return Task.FromResult(new WhatsAppTemplateSubmitResult(true, metaTemplateId, null, null));
        }
    }

    private sealed class MemoryStorage : IMediaStorageService
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public int Uploads { get; private set; }

        public Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            Uploads++;
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var key = $"{Guid.NewGuid():N}-{fileName}";
            Files[key] = copy.ToArray();
            return Task.FromResult(new MediaStorageResult(key, GetPublicUrl(key)));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            Files.Remove(storageKey);
            return Task.CompletedTask;
        }

        public string GetLocalPath(string storageKey) => $"/media/{storageKey}";

        public string GetPublicUrl(string storageKey) => $"https://cdn.example.test/{storageKey}";

        public bool IsPublicUrl(string url) => url.StartsWith("https://cdn.example.test");

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Files.TryGetValue(storageKey, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new FileNotFoundException(storageKey);
    }
}
