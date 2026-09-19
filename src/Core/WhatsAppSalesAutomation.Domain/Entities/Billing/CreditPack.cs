using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>A purchasable bundle of extra units for one <see cref="QuotaType"/> - global catalog,
/// priced in base USD like <see cref="Plan"/>. Retired with IsActive rather than deleted, so a past
/// purchase's <see cref="Payment"/> keeps pointing at something.</summary>
public class CreditPack : BaseEntity
{
    public QuotaType QuotaType { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Units { get; set; }

    public int PriceCents { get; set; }

    public bool IsActive { get; set; } = true;
}
