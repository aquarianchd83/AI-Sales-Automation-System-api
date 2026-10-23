using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>A feature a plan includes. Global, like <see cref="Plan"/>: a tenant on the plan can use the feature, a tenant not on it cannot - the
/// feature is hidden in the app and refused by the API. One row per (plan, feature).</summary>
public class PlanFeature : BaseEntity
{
    public Guid PlanId { get; set; }

    public PlatformFeature Feature { get; set; }
}
