using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>An explicit price for one plan in one country, in that country's own currency - global catalog data,
/// not tenant-owned. A tenant in a country with a row pays exactly this; a country without one falls back to the
/// plan's base USD price converted at the catalog rate. One row per (plan, country).</summary>
public class PlanPrice : BaseEntity
{
    public Guid PlanId { get; set; }

    /// <summary>ISO 3166-1 alpha-2, one of the codes in RegionalPricingCatalog.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>In the currency of <see cref="CountryCode"/> (see RegionalPricingCatalog), never USD cents.</summary>
    public decimal Amount { get; set; }
}

/// <summary>Same as <see cref="PlanPrice"/>, for a credit pack.</summary>
public class CreditPackPrice : BaseEntity
{
    public Guid CreditPackId { get; set; }

    public string CountryCode { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}
