using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Packages;

/// <summary>
/// A package the tenant sells to its own customers (a name, a price, how long it runs and what it includes).
/// Not to be confused with the platform's <c>Plan</c>, which is what the tenant pays the platform.
///
/// <see cref="ExpectedSales"/> is the tenant's own target number of customers buying this package in a
/// month; price x expected sales is the revenue projection the Packages screen shows.
/// </summary>
public class SalesPackage : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal Price { get; set; }

    public int DurationValue { get; set; } = 1;

    public PackageDurationUnit DurationUnit { get; set; } = PackageDurationUnit.Months;

    /// <summary>One feature per line. Exposed as a list by the DTOs; a single column keeps the table flat.</summary>
    public string? FeaturesText { get; set; }

    public int ExpectedSales { get; set; }

    public bool IsActive { get; set; } = true;
}
