namespace WhatsAppSalesAutomation.Application.Reports;

/// <summary>
/// The Revenue report. <paramref name="Granularity"/> is "Day" for the one-month view and "Month"
/// otherwise; <paramref name="Trend"/> has one point per bucket, empty buckets included, so the chart
/// has no gaps. Percent fields are null when their denominator is zero.
/// </summary>
public record RevenueReportDto(
    int Months,
    string Granularity,
    DateTime From,
    DateTime To,
    decimal TotalRevenue,
    int SalesCount,
    decimal AverageSale,
    int UniqueCustomers,
    decimal PreviousRevenue,
    int PreviousSalesCount,
    double? RevenueChangePercent,
    decimal ExpectedRevenue,
    double? TargetAchievedPercent,
    string? MostPopularPackage,
    string? TopRevenuePackage,
    IReadOnlyList<RevenueTrendPointDto> Trend,
    IReadOnlyList<PackageRevenueRowDto> Packages);

public record RevenueTrendPointDto(DateTime Start, decimal Revenue, int Sales);

/// <summary>
/// One package's result over the period. <paramref name="TargetSales"/> is the tenant's own expected
/// sales per month times the months in the period. <paramref name="Rank"/> is by sales count (1 = most
/// popular) and is 0 for a package with no sales.
/// </summary>
public record PackageRevenueRowDto(
    Guid PackageId,
    string Name,
    bool IsActive,
    decimal Price,
    int SalesCount,
    decimal Revenue,
    double? SalesSharePercent,
    double? RevenueSharePercent,
    int UniqueCustomers,
    int TargetSales,
    int Rank);
