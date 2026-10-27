using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A message sent through the QR bridge is not a Meta template send, so no Meta category charge applies to it.</summary>
public sealed class WhatsAppSpendBridgeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Customer _customer = new() { PhoneNumberE164 = "+919800000001" };
    private int _n;

    public WhatsAppSpendBridgeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.Customers.Add(_customer);
        _db.MessageTemplates.Add(new MessageTemplate { Name = "promo", WhatsAppTemplateName = "promo", Category = TemplateCategory.Marketing, BodyText = "Hi" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Send(string provider)
    {
        _db.Messages.Add(new Message
        {
            CustomerId = _customer.Id, Direction = MessageDirection.Outbound, MessageType = MessageType.Template, Status = MessageStatus.Delivered,
            TemplateName = "promo", WhatsAppMessageId = $"id{++_n}", Provider = provider, IdempotencyKey = $"k{_n}", CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private Task<WhatsAppSpend> SpendAsync()
    {
        var pricing = new WhatsAppPricingOptions { Default = new WhatsAppCategoryRates { Marketing = 0.10m, Utility = 0.02m, Authentication = 0.02m } };
        var service = new WhatsAppSpendService(_db, new Snap(pricing));
        return service.GetForTenantAsync(_tenant.Id, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task A_meta_template_send_is_billable_as_before()
    {
        Send(WhatsAppProviders.Meta);

        var spend = await SpendAsync();

        Assert.Equal(1, spend.BillableMessages);
        Assert.Equal(0.10m, spend.EstimatedCostUsd);
    }

    [Fact]
    public async Task A_bridge_send_is_counted_as_sent_but_carries_no_meta_charge()
    {
        Send(WhatsAppProviders.QrBridge);

        var spend = await SpendAsync();

        Assert.Equal(1, spend.MessagesSent);
        Assert.Equal(0, spend.BillableMessages);
        Assert.Equal(0m, spend.EstimatedCostUsd);
    }

    [Fact]
    public async Task Mixed_providers_charge_only_the_meta_sends()
    {
        Send(WhatsAppProviders.Meta);
        Send(WhatsAppProviders.QrBridge);
        Send(WhatsAppProviders.QrBridge);

        var spend = await SpendAsync();

        Assert.Equal(3, spend.MessagesSent);
        Assert.Equal(1, spend.BillableMessages);
        Assert.Equal(0.10m, spend.EstimatedCostUsd);
    }

    private sealed class Snap : IOptionsSnapshot<WhatsAppPricingOptions>
    {
        public Snap(WhatsAppPricingOptions value) => Value = value;
        public WhatsAppPricingOptions Value { get; }
        public WhatsAppPricingOptions Get(string? name) => Value;
    }

    private sealed class Ambient : ITenantContext
    {
        private readonly Guid _id;
        public Ambient(Guid id) => _id = id;
        public Guid? TenantId => _id;
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
