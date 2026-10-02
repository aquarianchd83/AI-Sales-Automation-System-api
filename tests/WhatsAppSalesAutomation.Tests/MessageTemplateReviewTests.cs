using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.MessageTemplates;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Meta owns the review status of a template that is on Meta; a manual review is only for one that is not.</summary>
public sealed class MessageTemplateReviewTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly MessageTemplateService _service;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };

    public MessageTemplateReviewTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        _service = new MessageTemplateService(_db, null!, null!, null!, new ReviewMessageTemplateRequestValidator());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MessageTemplate Add(string? metaTemplateId)
    {
        var template = new MessageTemplate
        {
            Name = "follow_1", WhatsAppTemplateName = "follow_1", BodyText = "Hi", MetaTemplateId = metaTemplateId,
            WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending,
        };
        _db.MessageTemplates.Add(template);
        _db.SaveChanges();
        return template;
    }

    [Fact]
    public async Task A_template_that_is_on_meta_cannot_be_approved_by_hand()
    {
        var template = Add("1387908590149880");

        var error = await Assert.ThrowsAsync<ConflictException>(() => _service.ReviewAsync(template.Id, new ReviewMessageTemplateRequest("Approved")));

        Assert.Contains("Meta decides", error.Message);
        Assert.Equal(WhatsAppTemplateStatus.Pending, (await _db.MessageTemplates.SingleAsync()).WhatsAppTemplateStatus);
    }

    [Fact]
    public async Task A_template_never_pushed_to_meta_can_still_be_reviewed_by_hand()
    {
        var template = Add(null);

        var result = await _service.ReviewAsync(template.Id, new ReviewMessageTemplateRequest("Approved"));

        Assert.Equal("Approved", result.WhatsAppTemplateStatus);
    }

    private sealed class AmbientTenant : ITenantContext
    {
        private readonly Guid _tenantId;
        public AmbientTenant(Guid tenantId) => _tenantId = tenantId;
        public Guid? TenantId => _tenantId;
        public bool IsPlatformSuperAdmin => false;
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
}

/// <summary>Meta's reclassification of a template (Utility to Marketing) is adopted by the sync.</summary>
public sealed class MessageTemplateCategorySyncTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private string _remoteCategory = "MARKETING";
    private string _remoteStatus = "APPROVED";

    public MessageTemplateCategorySyncTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant.Id), new Nobody()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private readonly RecordingNotifier _notices = new();

    private MessageTemplateService Service()
    {
        var whatsApp = Fake.Of<IWhatsAppService>((m, _) => m.Name == nameof(IWhatsAppService.GetMessageTemplatesAsync)
            ? Task.FromResult<IReadOnlyList<WhatsAppRemoteTemplate>>(new[] { new WhatsAppRemoteTemplate("m1", "create_template_from_portal", "en", _remoteStatus, _remoteCategory) })
            : throw new NotImplementedException(m.Name));
        return new MessageTemplateService(_db, whatsApp, null!, null!, null!, notifier: _notices);
    }

    private MessageTemplate Add(TemplateCategory category)
    {
        var template = new MessageTemplate
        {
            Name = "test", WhatsAppTemplateName = "create_template_from_portal", Language = "en", BodyText = "Hi",
            Category = category, MetaTemplateId = "m1", LastPushedBodyText = "Hi", WhatsAppTemplateStatus = WhatsAppTemplateStatus.Approved,
        };
        _db.MessageTemplates.Add(template);
        _db.SaveChanges();
        return template;
    }

    [Fact]
    public async Task Approval_by_meta_tells_the_tenant_and_says_when_it_reclassified_the_category()
    {
        var template = Add(TemplateCategory.Utility);
        template.WhatsAppTemplateStatus = WhatsAppTemplateStatus.Pending;
        await _db.SaveChangesAsync();

        await Service().SyncWithMetaAsync();

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal(TenantNotificationKind.TemplateApproved, notice.Kind);
        Assert.Equal(_tenant.Id, notice.TenantId);
        Assert.Contains("approved", notice.Title);
        Assert.Contains("Marketing", notice.Body);
        Assert.Contains("Utility", notice.Body);
    }

    [Theory]
    [InlineData("REJECTED", "rejected")]
    [InlineData("PAUSED", "paused")]
    [InlineData("DISABLED", "disabled")]
    public async Task A_template_meta_rejects_pauses_or_disables_needs_the_tenants_attention(string remoteStatus, string word)
    {
        Add(TemplateCategory.Marketing);
        _remoteStatus = remoteStatus;

        await Service().SyncWithMetaAsync();

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal(TenantNotificationKind.TemplateNeedsAttention, notice.Kind);
        Assert.Contains(word, notice.Title);
        Assert.Contains("inactive", notice.Body);
    }

    [Fact]
    public async Task A_sync_that_changes_nothing_is_silent_and_so_is_a_move_back_to_pending()
    {
        Add(TemplateCategory.Marketing);

        await Service().SyncWithMetaAsync();            // already approved here and there
        _remoteStatus = "PENDING";
        await Service().SyncWithMetaAsync();            // an appeal / edit: not news

        Assert.Empty(_notices.Sent);
    }

    [Fact]
    public async Task A_template_that_meta_pauses_and_later_approves_again_gives_two_notices()
    {
        Add(TemplateCategory.Marketing);
        _remoteStatus = "PAUSED";
        await Service().SyncWithMetaAsync();
        _remoteStatus = "APPROVED";
        await Service().SyncWithMetaAsync();

        Assert.Equal(new[] { TenantNotificationKind.TemplateNeedsAttention, TenantNotificationKind.TemplateApproved }, _notices.Sent.Select(n => n.Kind));
    }

    [Fact]
    public async Task A_template_meta_reclassified_to_marketing_follows_it_after_a_sync()
    {
        var template = Add(TemplateCategory.Utility);

        var result = await Service().SyncOneAsync(template.Id);

        Assert.Equal("Marketing", result.Template.Category);
        Assert.Equal(TemplateCategory.Marketing, (await _db.MessageTemplates.SingleAsync()).Category);
    }

    [Fact]
    public async Task A_template_whose_category_already_matches_is_left_alone()
    {
        var template = Add(TemplateCategory.Marketing);

        var result = await Service().SyncWithMetaAsync();

        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(0, result.StatusUpdatedCount);
        Assert.Equal(TemplateCategory.Marketing, template.Category);
    }

    [Fact]
    public async Task Changing_the_category_of_a_template_on_meta_points_to_sync()
    {
        var template = Add(TemplateCategory.Utility);
        var validator = new UpdateMessageTemplateRequestValidator();
        var service = new MessageTemplateService(_db, null!, null!, validator, null!);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateAsync(template.Id, new UpdateMessageTemplateRequest("Hi", true, Category: "Marketing")));

        Assert.Contains("Use Sync", error.Message);
    }

    private sealed class Ambient : ITenantContext
    {
        private readonly Guid _id;
        public Ambient(Guid id) => _id = id;
        public Guid? TenantId => _id;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class Nobody : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
