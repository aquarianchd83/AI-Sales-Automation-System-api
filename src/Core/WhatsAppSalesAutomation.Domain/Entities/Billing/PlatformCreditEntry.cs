using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>One immutable movement of the platform wallet. Append-only: a correction is a new entry.</summary>
public class PlatformCreditEntry : BaseEntity
{
    public QuotaType QuotaType { get; set; }

    public PlatformCreditEntryType EntryType { get; set; }

    /// <summary>Signed: positive adds units, negative removes them.</summary>
    public decimal UnitsDelta { get; set; }

    public decimal BalanceAfter { get; set; }

    /// <summary>The spend's operation key, so a retry is a no-op and a reversal finds what it undoes. Empty for manual entries.</summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>The tenant whose action it paid for.</summary>
    public Guid? TenantId { get; set; }

    public string? Note { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
