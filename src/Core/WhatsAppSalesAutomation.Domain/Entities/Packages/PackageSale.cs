using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Packages;

/// <summary>
/// One sale of a <see cref="SalesPackage"/> to a customer. This is the record behind the Revenue report:
/// "revenue" is the sum of <see cref="Amount"/> and "most popular" is the package with the most rows.
///
/// <see cref="Amount"/> is what was actually charged, copied from the package price at the time (and
/// editable, since discounts happen), so later price changes never rewrite history. A package with sales
/// cannot be deleted - deactivate it instead - so the report can always name the package.
/// </summary>
public class PackageSale : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid PackageId { get; set; }

    /// <summary>The buying customer, when the tenant chose one. Optional: a walk-in sale still counts.</summary>
    public Guid? CustomerId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>When the sale happened, UTC. Defaults to now but can be backdated to record an earlier sale.</summary>
    public DateTime SoldAt { get; set; }
}
