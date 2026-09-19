using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One immutable movement of units for one tenant and quota type. Append-only: never updated or
/// deleted, a correction is a new entry that references the one it corrects. The sum of UnitsDelta over
/// a tenant's entries for a type always equals the sum of that tenant's grants' remaining units plus
/// nothing else - the nightly reconciliation asserts exactly that.
/// </summary>
public class QuotaLedgerEntry : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public QuotaType QuotaType { get; set; }

    public QuotaEntryType EntryType { get; set; }

    /// <summary>Signed: positive adds units, negative removes them.</summary>
    public decimal UnitsDelta { get; set; }

    /// <summary>The tenant's usable balance for this quota type immediately after this entry.</summary>
    public decimal BalanceAfter { get; set; }

    public Guid? GrantId { get; set; }

    /// <summary>Groups the entries one call produced (a consumption spread over two grants writes two)
    /// and is what makes a retry a no-op instead of a double charge.</summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>What caused it - "Message", "AiInteraction", "LeadDiscoveryRun", "Payment", "Admin"...</summary>
    public string? ReferenceType { get; set; }

    public string? ReferenceId { get; set; }

    /// <summary>Free text: the reason for an adjustment, or the fresh/duplicate/rejected split of a
    /// lead-discovery run.</summary>
    public string? Note { get; set; }

    /// <summary>The platform admin behind a manual entry - null for system entries.</summary>
    public Guid? ActorUserId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
