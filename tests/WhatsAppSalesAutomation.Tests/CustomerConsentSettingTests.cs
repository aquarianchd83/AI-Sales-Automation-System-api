using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The tenant's "is consent required" setting decides the status a new customer starts in.</summary>
public sealed class CustomerConsentSettingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly CustomerService _service;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };

    public CustomerConsentSettingTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        _service = new CustomerService(
            _db, null!, new Clock(),
            new CreateCustomerRequestValidator(), new UpdateCustomerRequestValidator(),
            new BulkDeleteCustomersRequestValidator(), new OptInCustomerRequestValidator(),
            new BulkAddCustomerTagsRequestValidator(), new Ambient(_tenant.Id));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CreateCustomerRequest Request(string phone) => new(phone, "Asha", null, null, "Manual", null, null);

    [Fact]
    public async Task Consent_is_not_required_by_default_and_a_new_customer_is_pending_opt_in()
    {
        Assert.False((await _service.GetConsentSettingAsync()).ConsentRequired);

        var customer = await _service.CreateAsync(Request("+919800000001"));

        Assert.Equal("PendingOptIn", customer.OptInStatus);
    }

    [Fact]
    public async Task When_consent_is_required_a_new_customer_is_waiting_for_consent()
    {
        await _service.SetConsentSettingAsync(new SetCustomerConsentSettingRequest(true));

        var customer = await _service.CreateAsync(Request("+919800000002"));

        Assert.True((await _service.GetConsentSettingAsync()).ConsentRequired);
        Assert.Equal("WaitingForConsent", customer.OptInStatus);
        Assert.Null(customer.OptInTimestamp);
    }

    [Fact]
    public async Task Changing_the_setting_leaves_existing_customers_as_they_are()
    {
        var before = await _service.CreateAsync(Request("+919800000003"));

        await _service.SetConsentSettingAsync(new SetCustomerConsentSettingRequest(true));

        Assert.Equal("PendingOptIn", (await _service.GetByIdAsync(before.Id)).OptInStatus);
    }

    [Fact]
    public async Task Recording_consent_makes_a_waiting_customer_opted_in()
    {
        await _service.SetConsentSettingAsync(new SetCustomerConsentSettingRequest(true));
        var customer = await _service.CreateAsync(Request("+919800000004"));

        var optedIn = await _service.OptInAsync(customer.Id, new OptInCustomerRequest("WebsiteForm", null, "form-1", "Marketing"));

        Assert.Equal("OptedIn", optedIn.OptInStatus);
    }

    [Fact]
    public async Task The_history_reads_discovered_then_submitted_then_the_outcome_even_when_they_share_a_timestamp()
    {
        var customer = await _service.CreateAsync(Request("+919800000005"));
        var at = new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
        // Saved in the reverse of the order they happened in.
        foreach (var type in new[]
                 {
                     CustomerHistoryEventTypes.VerificationPassed, CustomerHistoryEventTypes.VerificationSubmitted, CustomerHistoryEventTypes.Discovered
                 })
        {
            _db.CustomerHistoryEvents.Add(new CustomerHistoryEvent { TenantId = _tenant.Id, CustomerId = customer.Id, EventType = type, OccurredAtUtc = at });
            await _db.SaveChangesAsync();
        }

        var history = await _service.GetHistoryAsync(customer.Id);

        Assert.Equal(
            new[] { CustomerHistoryEventTypes.Discovered, CustomerHistoryEventTypes.VerificationSubmitted, CustomerHistoryEventTypes.VerificationPassed },
            history.Select(e => e.EventType));
    }

    [Fact]
    public async Task Customer_discovered_is_always_the_first_step_and_no_entry_is_dated_later_than_the_one_below_it()
    {
        var customer = await _service.CreateAsync(Request("+919800000006"));
        var verified = new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
        // History written before the order was fixed: discovered was stamped after the check it came before.
        _db.CustomerHistoryEvents.AddRange(
            new CustomerHistoryEvent { TenantId = _tenant.Id, CustomerId = customer.Id, EventType = CustomerHistoryEventTypes.Discovered, OccurredAtUtc = verified.AddSeconds(1) },
            new CustomerHistoryEvent { TenantId = _tenant.Id, CustomerId = customer.Id, EventType = CustomerHistoryEventTypes.VerificationSubmitted, OccurredAtUtc = verified },
            new CustomerHistoryEvent { TenantId = _tenant.Id, CustomerId = customer.Id, EventType = CustomerHistoryEventTypes.VerificationPassed, OccurredAtUtc = verified });
        await _db.SaveChangesAsync();

        var history = await _service.GetHistoryAsync(customer.Id);

        Assert.Equal(
            new[] { CustomerHistoryEventTypes.Discovered, CustomerHistoryEventTypes.VerificationSubmitted, CustomerHistoryEventTypes.VerificationPassed },
            history.Select(e => e.EventType));
        Assert.Equal(verified, history[0].OccurredAtUtc);   // not 1s after the entries below it
        Assert.True(history.Zip(history.Skip(1), (a, b) => a.OccurredAtUtc <= b.OccurredAtUtc).All(ok => ok));
    }

    [Fact]
    public void The_policy_maps_the_setting_to_the_status()
    {
        Assert.Equal(OptInStatus.WaitingForConsent, ConsentPolicy.InitialStatus(true));
        Assert.Equal(OptInStatus.PendingOptIn, ConsentPolicy.InitialStatus(false));
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 10, 15, 6, 0, 0);
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
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
}
