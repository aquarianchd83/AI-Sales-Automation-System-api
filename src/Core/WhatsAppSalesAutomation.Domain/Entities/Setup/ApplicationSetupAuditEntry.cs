using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Setup;

/// <summary>
/// Append-only history of an application's setup: every answer set or cleared (with the previous and new
/// value), every completion, plan change, migration and execution - each with who, when and which plan
/// version it happened under, so an administrator can say exactly what configuration a run used.
///
/// A trail the people it records can edit is not a trail: <c>AuditTrailSaveChangesInterceptor</c> refuses to
/// save a modified or deleted row, same as the tenant audit log.
/// </summary>
public class ApplicationSetupAuditEntry : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ApplicationId { get; set; }

    public Guid PlanSetupVersionId { get; set; }

    /// <summary>Denormalised ("Lead Generation v2") so the history reads correctly after the plan is renamed.</summary>
    public string PlanVersionLabel { get; set; } = string.Empty;

    public SetupAuditAction Action { get; set; }

    public string? FieldKey { get; set; }

    public string? PreviousValue { get; set; }

    public string? NewValue { get; set; }

    public string? Reason { get; set; }

    /// <summary>Null for the system itself.</summary>
    public Guid? PerformedBy { get; set; }

    public Guid? ImpersonatedBy { get; set; }

    public DateTime PerformedAt { get; set; }
}
