using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Packages;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The packages a tenant sells: create/edit/delete, per-tenant unique names, and the revenue projection.</summary>
public sealed class PackageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly PackageService _service;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestClock _clock = new();

    public PackageServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();
        _service = new PackageService(_db, new SavePackageRequestValidator(), new RecordPackageSaleRequestValidator(), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static SavePackageRequest Request(string name = "Gold", decimal price = 5000m, int expectedSales = 4, bool active = true, params string[] features) =>
        new(name, null, price, 3, PackageDurationUnit.Months, features, expectedSales, active);

    [Fact]
    public async Task Create_trims_and_drops_blank_features_and_projects_revenue()
    {
        var dto = await _service.CreateAsync(Request("  Gold ", 5000m, 4, true, " SEO ", "", "Ads"));

        Assert.Equal("Gold", dto.Name);
        Assert.Equal(new[] { "SEO", "Ads" }, dto.Features);
        Assert.Equal(20000m, dto.ProjectedRevenue);
    }

    [Fact]
    public async Task Duplicate_name_is_refused_case_insensitively_but_renaming_to_itself_is_fine()
    {
        var gold = await _service.CreateAsync(Request("Gold"));
        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateAsync(Request("gold")));

        var updated = await _service.UpdateAsync(gold.Id, Request("GOLD", 6000m));
        Assert.Equal(6000m, updated.Price);
    }

    [Fact]
    public async Task Summary_counts_only_active_packages()
    {
        await _service.CreateAsync(Request("Gold", 5000m, 4));
        await _service.CreateAsync(Request("Silver", 2000m, 10));
        await _service.CreateAsync(Request("Retired", 9000m, 99, active: false));

        var summary = await _service.GetSummaryAsync();

        Assert.Equal(2, summary.ActivePackages);
        Assert.Equal(14, summary.TotalExpectedSales);
        Assert.Equal(40000m, summary.TotalProjectedRevenue);
    }

    [Fact]
    public async Task Negative_price_is_rejected_and_delete_removes_the_package()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(Request(price: -1m)));

        var dto = await _service.CreateAsync(Request());
        await _service.DeleteAsync(dto.Id);

        var page = await _service.GetPagedAsync(new PagedRequest());
        Assert.Empty(page.Items);
        await Assert.ThrowsAsync<NotFoundException>(() => _service.GetByIdAsync(dto.Id));
    }

    [Fact]
    public async Task Recording_a_sale_defaults_to_the_package_price_and_now()
    {
        var gold = await _service.CreateAsync(Request("Gold", 5000m));

        var sale = await _service.RecordSaleAsync(new RecordPackageSaleRequest(gold.Id, null, null, null));

        Assert.Equal(5000m, sale.Amount);
        Assert.Equal(_clock.UtcNow, sale.SoldAt);
        Assert.Equal("Gold", sale.PackageName);
    }

    [Fact]
    public async Task A_discounted_amount_and_an_earlier_date_are_kept()
    {
        var gold = await _service.CreateAsync(Request("Gold", 5000m));
        var earlier = _clock.UtcNow.AddDays(-40);

        var sale = await _service.RecordSaleAsync(new RecordPackageSaleRequest(gold.Id, null, 4500m, earlier));

        Assert.Equal(4500m, sale.Amount);
        Assert.Equal(earlier, sale.SoldAt);
    }

    [Fact]
    public async Task A_future_dated_sale_is_rejected()
    {
        var gold = await _service.CreateAsync(Request("Gold"));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.RecordSaleAsync(new RecordPackageSaleRequest(gold.Id, null, null, _clock.UtcNow.AddDays(10))));
    }

    [Fact]
    public async Task A_sale_for_an_unknown_package_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.RecordSaleAsync(new RecordPackageSaleRequest(Guid.NewGuid(), null, null, null)));
    }

    [Fact]
    public async Task A_package_with_sales_cannot_be_deleted_until_the_sale_is_removed()
    {
        var gold = await _service.CreateAsync(Request("Gold"));
        var sale = await _service.RecordSaleAsync(new RecordPackageSaleRequest(gold.Id, null, null, null));

        await Assert.ThrowsAsync<ConflictException>(() => _service.DeleteAsync(gold.Id));

        await _service.DeleteSaleAsync(sale.Id);
        await _service.DeleteAsync(gold.Id);
        Assert.Empty((await _service.GetPagedAsync(new PagedRequest())).Items);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
