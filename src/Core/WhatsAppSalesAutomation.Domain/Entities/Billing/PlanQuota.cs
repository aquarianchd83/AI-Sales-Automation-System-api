using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>How many units of one <see cref="QuotaType"/> a plan includes per billing period - global,
/// like <see cref="Plan"/>, not tenant-owned. One row per (plan, quota type); a plan with no row for a
/// type includes none of it.</summary>
public class PlanQuota : BaseEntity
{
    public Guid PlanId { get; set; }

    public QuotaType QuotaType { get; set; }

    public decimal IncludedUnits { get; set; }
}
