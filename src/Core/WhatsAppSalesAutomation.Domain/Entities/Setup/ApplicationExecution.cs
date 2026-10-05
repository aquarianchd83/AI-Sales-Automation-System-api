using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Setup;

/// <summary>
/// One run of an application, carrying a frozen copy of the configuration it ran with. This is how "apply
/// changes from the next execution" is made true rather than just promised: whatever consumes a run reads
/// <see cref="SetupSnapshotJson"/>, so editing the live setup afterwards cannot change a run that has
/// already started, and the audit question "what did that run use?" has a single, unambiguous answer.
/// </summary>
public class ApplicationExecution : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ApplicationId { get; set; }

    public Guid PlanId { get; set; }

    public Guid PlanSetupVersionId { get; set; }

    public string PlanVersionLabel { get; set; } = string.Empty;

    /// <summary>JSON object of fieldKey -> answer, for the fields that applied (visible under the answers given).</summary>
    public string SetupSnapshotJson { get; set; } = "{}";

    public Guid? StartedBy { get; set; }

    public DateTime StartedAt { get; set; }
}
