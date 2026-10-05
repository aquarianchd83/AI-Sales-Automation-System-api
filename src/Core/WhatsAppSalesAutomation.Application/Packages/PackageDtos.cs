using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Packages;

/// <summary><paramref name="ProjectedRevenue"/> is price x expected sales per month.</summary>
public record PackageDto(
    Guid Id,
    string Name,
    string? Description,
    decimal Price,
    int DurationValue,
    PackageDurationUnit DurationUnit,
    IReadOnlyList<string> Features,
    int ExpectedSales,
    decimal ProjectedRevenue,
    bool IsActive,
    DateTime CreatedAt);

public record SavePackageRequest(
    string Name,
    string? Description,
    decimal Price,
    int DurationValue,
    PackageDurationUnit DurationUnit,
    IReadOnlyList<string>? Features,
    int ExpectedSales,
    bool IsActive);

/// <summary>Totals over the tenant's active packages.</summary>
public record PackageSummaryDto(int ActivePackages, int TotalExpectedSales, decimal TotalProjectedRevenue);
