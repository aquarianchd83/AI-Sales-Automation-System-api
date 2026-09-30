using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.MessageTemplates;
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
