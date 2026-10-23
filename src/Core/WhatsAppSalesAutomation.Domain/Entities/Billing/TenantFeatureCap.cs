using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>The most credits one feature may spend in a calendar month, set by the tenant itself. It protects the shared wallet from one feature
/// using it all; with no row the feature is limited only by the wallet. Resets on the first of each month (UTC).</summary>
public class TenantFeatureCap : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public PlatformFeature Feature { get; set; }

    public decimal MonthlyLimit { get; set; }
}
