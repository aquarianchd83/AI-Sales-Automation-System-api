using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// How many free uses of a feature outside its plan a tenant has had (see <see cref="PlanFeatureTrial"/>). Lifetime: it never resets, and a tenant
/// that upgrades and later comes back to a plan without the feature picks up where it left off. Only uses made while the feature was NOT in the
/// tenant's plan are counted. One row per (tenant, feature).
/// </summary>
public class TenantFeatureTrialUsage : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public PlatformFeature Feature { get; set; }

    public int Used { get; set; }

    public DateTime? FirstUsedAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
}
