using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Setup;

/// <summary>
/// A tenant's application of one platform plan - the thing that is set up, then run. Pinned to a
/// <see cref="PlanSetupVersion"/> when it is created so later edits to the plan's requirements cannot change
/// what it asks or how it executes; it moves to a newer version only through an explicit migration.
///
/// <see cref="SetupStatus"/> is stored (so lists and the audit log can filter on it) but is always recomputed
/// from the answers whenever they change - and again when the application is executed, so a stale value can
/// never let an incomplete setup run.
/// </summary>
public class PlanApplication : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid PlanId { get; set; }

    public Guid PlanSetupVersionId { get; set; }

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Active;

    public ApplicationSetupStatus SetupStatus { get; set; } = ApplicationSetupStatus.NotStarted;

    /// <summary>When the setup was last confirmed complete. Null = never has been, which is what separates
    /// "still in progress" from "was complete and needs attention".</summary>
    public DateTime? SetupCompletedAt { get; set; }

    public Guid? SetupCompletedBy { get; set; }

    /// <summary>When the confirmation lapses, if the plan version sets a validity period.</summary>
    public DateTime? SetupExpiresAt { get; set; }

    public DateTime? LastExecutedAt { get; set; }

    public int ExecutionCount { get; set; }

    public Guid? CreatedBy { get; set; }
}
