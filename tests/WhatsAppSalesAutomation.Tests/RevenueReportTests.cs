using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Reports;
using WhatsAppSalesAutomation.Domain.Entities.Packages;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Revenue report: period maths, the comparison with the previous period, the trend and the popularity ranking.</summary>
public sealed class RevenueReportTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly ReportService _service;
    private readonly SalesPackage _gold = new() { Name = "Gold", Price = 5000m, ExpectedSales = 4 };
    private readonly SalesPackage _silver = new() { Name = "Silver", Price = 2000m, ExpectedSales = 10 };
    private readonly SalesPackage _unsold = new() { Name = "Platinum", Price = 9000m, ExpectedSales = 1 };

    public RevenueReportTests()
    {
        _connection.Open();
        _db = new SqliteApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options,
            new Ambient(Tenant), new AnonymousUser()) { StampTenantId = Tenant };
        _db.Database.EnsureCreated();
        _service = new ReportService(_db, new TestClock { UtcNow = Now });

        foreach (var p in new[] { _gold, _silver, _unsold })
            p.TenantId = Tenant;
        _db.SalesPackages.AddRange(_gold, _silver, _unsold);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Sell(SalesPackage package, DateTime soldAt, decimal? amount = null, Guid? customerId = null)
    {
        _db.PackageSales.Add(new PackageSale
        {
            TenantId = Tenant,
            PackageId = package.Id,
            CustomerId = customerId,
            Amount = amount ?? package.Price,
            SoldAt = soldAt,
        });
        _db.SaveChanges();
    }

    private void SeedThreeMonths()
    {
        Sell(_gold, new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc));
        Sell(_gold, new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc));
        Sell(_silver, new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc));
        Sell(_silver, new DateTime(2026, 8, 6, 9, 0, 0, DateTimeKind.Utc));
        Sell(_silver, new DateTime(2026, 8, 7, 9, 0, 0, DateTimeKind.Utc));
        Sell(_gold, new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc));
        // Previous period (Apr-Jun) and one older than that, which must be ignored entirely.
        Sell(_silver, new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc));
        Sell(_gold, new DateTime(2025, 1, 10, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Three_months_totals_the_current_period_and_compares_it_with_the_one_before()
    {
        SeedThreeMonths();

        var report = await _service.GetRevenueAsync(3);

        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), report.From);
        Assert.Equal(21000m, report.TotalRevenue);
        Assert.Equal(6, report.SalesCount);
        Assert.Equal(3500m, report.AverageSale);
        Assert.Equal(2000m, report.PreviousRevenue);
        Assert.Equal(1, report.PreviousSalesCount);
        Assert.Equal(950.0, report.RevenueChangePercent);
    }

    [Fact]
    public async Task Packages_are_ranked_by_sales_with_revenue_breaking_a_tie()
    {
        SeedThreeMonths();

        var report = await _service.GetRevenueAsync(3);

        Assert.Equal(new[] { "Gold", "Silver", "Platinum" }, report.Packages.Select(p => p.Name));
        Assert.Equal(new[] { 1, 2, 0 }, report.Packages.Select(p => p.Rank));
        Assert.Equal("Gold", report.MostPopularPackage);
        Assert.Equal("Gold", report.TopRevenuePackage);
        Assert.Equal(50.0, report.Packages[0].SalesSharePercent);
        Assert.Equal(0, report.Packages[2].SalesCount);
    }

    [Fact]
    public async Task The_trend_has_one_point_per_month_with_empty_months_included()
    {
        Sell(_gold, new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc));

        var report = await _service.GetRevenueAsync(3);

        Assert.Equal("Month", report.Granularity);
        Assert.Equal(new[] { 0m, 0m, 5000m }, report.Trend.Select(t => t.Revenue));
        Assert.Equal(new[] { 7, 8, 9 }, report.Trend.Select(t => t.Start.Month));
    }

    [Fact]
    public async Task One_month_is_charted_by_day_up_to_today()
    {
        Sell(_gold, new DateTime(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc));

        var report = await _service.GetRevenueAsync(1);

        Assert.Equal("Day", report.Granularity);
        Assert.Equal(21, report.Trend.Count);
        Assert.Equal(5000m, report.Trend[^1].Revenue);
    }

    [Fact]
    public async Task Expected_revenue_is_the_active_packages_monthly_target_times_the_months()
    {
        // Gold 5000x4 + Silver 2000x10 + Platinum 9000x1 = 49000 a month; over 3 months 147000.
        Sell(_gold, new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc));

        var report = await _service.GetRevenueAsync(3);

        Assert.Equal(147000m, report.ExpectedRevenue);
        Assert.Equal(3.4, report.TargetAchievedPercent);
    }

    [Fact]
    public async Task With_no_sales_the_percentages_are_null_not_zero_or_a_crash()
    {
        var report = await _service.GetRevenueAsync(6);

        Assert.Equal(0m, report.TotalRevenue);
        Assert.Equal(0m, report.AverageSale);
        Assert.Null(report.RevenueChangePercent);
        Assert.Null(report.MostPopularPackage);
        Assert.All(report.Packages, p => Assert.Null(p.SalesSharePercent));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 12)]
    [InlineData(99, 24)]
    public async Task The_period_is_clamped_to_1_through_24_months(int asked, int expected)
    {
        var report = await _service.GetRevenueAsync(asked);

        Assert.Equal(expected, report.Months);
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
