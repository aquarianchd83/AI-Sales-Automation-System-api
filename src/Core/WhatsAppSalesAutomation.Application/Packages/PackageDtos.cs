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
    DateTime CreatedAt,
    Guid? PlatformPlanId = null);

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

/// <summary>One recorded sale. <paramref name="CustomerName"/> is null for a sale with no customer attached.</summary>
public record PackageSaleDto(
    Guid Id,
    Guid PackageId,
    string PackageName,
    Guid? CustomerId,
    string? CustomerName,
    decimal Amount,
    DateTime SoldAt);

/// <summary>Amount and SoldAt are optional: the package price and the current time are used when omitted.</summary>
public record RecordPackageSaleRequest(Guid PackageId, Guid? CustomerId, decimal? Amount, DateTime? SoldAt);
