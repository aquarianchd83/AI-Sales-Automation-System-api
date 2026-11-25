using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The plan's customer limit (Plan.MaxCustomers): adding by hand and importing stop at it; 0 is no limit.</summary>
public sealed class CustomerLimitTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Plan _basic = new() { Code = PlatformFeatureCatalog.TrialPlanCode, Name = "Basic", IsActive = true, MaxCustomers = 2 };
    private readonly FakeImport _import = new();
    private readonly CustomerService _service;

    public CustomerLimitTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _db.Plans.Add(_basic);
        _db.Subscriptions.Add(new Subscription { TenantId = _tenant, PlanId = _basic.Id, Status = SubscriptionStatus.Active });
        _db.SaveChanges();

        _service = new CustomerService(
            _db, _import, new TestClock(),
            new CreateCustomerRequestValidator(), new UpdateCustomerRequestValidator(),
            new BulkDeleteCustomersRequestValidator(), new OptInCustomerRequestValidator(),
            new BulkAddCustomerTagsRequestValidator(), new Ambient(_tenant));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<CustomerDto> AddAsync(string phone) =>
        _service.CreateAsync(new CreateCustomerRequest(phone, "Test", null, null, null, null, null));

    [Fact]
    public async Task Adding_by_hand_stops_at_the_plans_customer_limit()
    {
        await AddAsync("+919820000001");
        await AddAsync("+919820000002");

        var ex = await Assert.ThrowsAsync<PlanLimitExceededException>(() => AddAsync("+919820000003"));
        Assert.Contains("up to 2 customers", ex.Message);

        var quota = await CustomerQuota.GetAsync(_db, _tenant);
        Assert.Equal((2, 2, true), (quota!.Limit, quota.Used, quota.Exhausted));
    }

    [Fact]
    public async Task An_import_takes_rows_up_to_the_limit_and_reports_the_rest()
    {
        _import.Rows = Enumerable.Range(1, 4)
            .Select(i => new CustomerImportRow(i + 1, $"+91982000001{i}", "Row" + i, null, null, Array.Empty<string>()))
            .ToList();

        var result = await _service.ImportAsync(Stream.Null, "customers.csv");

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(2, result.RowErrors.Count);
        Assert.All(result.RowErrors, e => Assert.Contains("up to 2 customers", e.Reason));
    }

    [Fact]
    public async Task Zero_is_no_limit()
    {
        _basic.MaxCustomers = 0;
        await _db.SaveChangesAsync();

        for (var i = 0; i < 3; i++)
            await AddAsync($"+91982000002{i}");

        Assert.Null(await CustomerQuota.GetAsync(_db, _tenant));
    }

    private sealed class FakeImport : ICustomerImportService
    {
        public IReadOnlyList<CustomerImportRow> Rows { get; set; } = Array.Empty<CustomerImportRow>();

        public Task<IReadOnlyList<CustomerImportRow>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
