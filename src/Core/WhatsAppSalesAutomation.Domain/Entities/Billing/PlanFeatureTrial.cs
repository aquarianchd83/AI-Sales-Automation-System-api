using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// A free taste of a feature a plan does NOT include: a tenant on the plan may use the feature <see cref="FreeUses"/> times (counted in the
/// feature's own unit - leads for lead discovery, AI generations for Content Studio, and so on; see PlatformFeatureCatalog.TrialUnitFor) and is
/// then asked to upgrade. Global, like <see cref="PlanFeature"/>, and edited on the platform admin's plan editor. One row per (plan, feature);
/// a row for a feature the plan already includes is ignored. Zero means no free uses (the row is kept so the seeder does not put a default back).
/// </summary>
public class PlanFeatureTrial : BaseEntity
{
    public Guid PlanId { get; set; }

    public PlatformFeature Feature { get; set; }

    public int FreeUses { get; set; }
}
