using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Meta scopes a template name by WhatsApp Business account and every tenant has its own, so the name is unique per tenant, not across them.</summary>
public sealed class MessageTemplateNameUniquenessTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _first = new() { Name = "Acme", Slug = "acme" };
    private readonly Tenant _second = new() { Name = "Globex", Slug = "globex" };

    public MessageTemplateNameUniquenessTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new AmbientTenant(_first.Id), new NoUser()) { StampTenantId = _first.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.AddRange(_first, _second);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void AddTemplate(Guid tenantId)
    {
        _db.StampTenantId = tenantId;
        _db.MessageTemplates.Add(new MessageTemplate { Name = "initial_message", WhatsAppTemplateName = "initial_message", Language = "en", BodyText = "Hi" });
        _db.SaveChanges();
    }

    [Fact]
    public void Two_tenants_can_use_the_same_template_name_and_language()
    {
        AddTemplate(_first.Id);

        AddTemplate(_second.Id);

        Assert.Equal(2, _db.MessageTemplates.IgnoreQueryFilters().Count(t => t.WhatsAppTemplateName == "initial_message"));
    }

    [Fact]
    public void One_tenant_still_cannot_use_the_same_name_and_language_twice()
    {
        AddTemplate(_first.Id);

        Assert.Throws<DbUpdateException>(() => AddTemplate(_first.Id));
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
