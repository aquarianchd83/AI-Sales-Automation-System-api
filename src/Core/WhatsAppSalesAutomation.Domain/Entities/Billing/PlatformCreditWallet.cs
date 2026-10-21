using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// The platform's own balance for one quota type - what is spent when a scenario's payer is <see cref="CreditPayer.Platform"/>. Not a
/// tenant's: there is exactly one row per quota type. Its concurrency token serialises two simultaneous spends, like <see cref="QuotaWallet"/>.
/// </summary>
public class PlatformCreditWallet : BaseEntity
{
    public QuotaType QuotaType { get; set; }

    public decimal Balance { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
