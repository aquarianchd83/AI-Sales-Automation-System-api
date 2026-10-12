using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// One metered step of the AI Content Studio, as the tenant sees it on its activity screen: what was done, what it
/// cost in quota units, what became of it. Written by <c>IStudioMeteringService</c> next to the quota ledger entry
/// it caused (<see cref="OperationKey"/> joins the two), so the tenant can reconcile every unit spent.
///
/// Append-mostly: a row is created when the work is charged (or refused) and its <see cref="Status"/> moves once, to
/// Completed or Refunded. It is never deleted by users.
/// </summary>
public class StudioActivity : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public StudioActivityKind Kind { get; set; }

    public StudioActivityStatus Status { get; set; }

    /// <summary>Plain-language line for the tenant, e.g. "Script for 'Cataract surgery myths'".</summary>
    public string Description { get; set; } = string.Empty;

    public QuotaType QuotaType { get; set; }

    /// <summary>Units spent. Zero for a blocked request.</summary>
    public decimal Units { get; set; }

    /// <summary>Units given back (equal to <see cref="Units"/> once refunded).</summary>
    public decimal UnitsRefunded { get; set; }

    /// <summary>The tenant's balance of this quota type right after the charge.</summary>
    public decimal BalanceAfter { get; set; }

    /// <summary>Unique per real-world event (a render job id, a generation request id). Retrying the same key never
    /// charges twice. Joins this row to its quota ledger entries.</summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>What the work belongs to - "ContentProject", "RenderJob"... - and its id, for linking from the screen.</summary>
    public string? ReferenceType { get; set; }

    public string? ReferenceId { get; set; }

    /// <summary>The outside service that did the work ("Shotstack", "Google", "OpenAI"), when there was one.</summary>
    public string? Provider { get; set; }

    /// <summary>Why a blocked request was refused or a refunded one failed. Never contains a token or key.</summary>
    public string? Detail { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
