using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One bucket of prepaid units with its own expiry. A period's plan allocation, a purchased credit
/// pack and a manual adjustment each become their own grant, because each expires and refunds
/// differently. <see cref="UnitsRemaining"/> is the only mutable number on it; every change to it has a
/// matching <see cref="QuotaLedgerEntry"/>.
/// </summary>
public class QuotaGrant : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public QuotaType QuotaType { get; set; }

    public QuotaGrantOrigin Origin { get; set; }

    public decimal UnitsGranted { get; set; }

    public decimal UnitsRemaining { get; set; }

    public DateTime GrantedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>The <see cref="Payment"/> that paid for it - null for adjustments.</summary>
    public Guid? PaymentId { get; set; }

    /// <summary>Set when the expiry pass has written off what was left, so it never runs twice.</summary>
    public DateTime? ExpiredProcessedAtUtc { get; set; }
}
