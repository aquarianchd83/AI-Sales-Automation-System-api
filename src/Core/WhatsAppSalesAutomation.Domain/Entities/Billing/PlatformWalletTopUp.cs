using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>Money the platform admin put into the platform wallet for one AI provider, on a given date. Append-only: the wallet balance is the sum of these.
/// Not owned by any tenant.</summary>
public class PlatformWalletTopUp : BaseEntity
{
    /// <summary>The date the money was added, as the admin entered it - not when the row was saved.</summary>
    public DateOnly TopUpDate { get; set; }

    public AiModelProvider Provider { get; set; }

    /// <summary>The amount in rupees.</summary>
    public decimal AmountInr { get; set; }

    public Guid? ActorUserId { get; set; }
}
