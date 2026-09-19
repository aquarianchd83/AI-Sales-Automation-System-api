using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// The cached usable balance for one tenant and quota type, and the row every ledger operation
/// touches. Its concurrency token is what serialises two simultaneous spends: the second one's save
/// fails, re-reads the grants and retries, so two sends can never both spend the same last unit. The
/// balance is always recomputed from the grants, never incremented, so it heals itself.
/// </summary>
public class QuotaWallet : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public QuotaType QuotaType { get; set; }

    public decimal Balance { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
