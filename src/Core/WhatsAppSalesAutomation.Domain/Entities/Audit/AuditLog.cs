using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Audit;

/// <summary>What happened to the audited entity. StatusChange is an Update that moved a lifecycle
/// property (Status, Stage or Mode) - split out because "who moved this lead to Won" is the question
/// asked of an audit log far more often than "who edited this lead".</summary>
public enum AuditAction
{
    Create = 0,
    Update = 1,
    Delete = 2,
    StatusChange = 3
}

/// <summary>
/// One tenant-visible record of a change to something worth accounting for - a lead's stage, a
/// campaign's status, a conversation's mode, a handoff's assignee, a customer's opt-in.
///
/// Append-only: <c>AuditTrailSaveChangesInterceptor</c> refuses to save a modified or deleted row. A
/// trail that its subjects can edit is not a trail.
///
/// This is the TENANT's audit log. The platform team's log of what a PlatformSuperAdmin did across
/// tenants is a separate table (<c>PlatformAuditLogEntry</c>) with a separate audience.
/// </summary>
public class AuditLog : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>A stable friendly name ("Lead", "Campaign"), not the CLR type name, so renaming a
    /// class does not orphan its history.</summary>
    public string EntityName { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>JSON of ONLY the allow-listed properties. Create: {"Stage":"New"}. Update:
    /// {"Stage":{"from":"New","to":"Qualified"}}. Never the whole entity - see the catalogue's doc
    /// comment for what that would leak.</summary>
    public string ChangesJson { get; set; } = "{}";

    /// <summary>NULL for a change made by the system itself - a background job, the AI agent, a
    /// webhook - which is a different fact from "an unknown user" and is displayed as such.</summary>
    public Guid? PerformedBy { get; set; }

    /// <summary>Set when a PlatformSuperAdmin made the change through an impersonated support session.
    /// The tenant is entitled to see that their data was touched by the platform, and by whom.</summary>
    public Guid? ImpersonatedBy { get; set; }

    public DateTime PerformedAt { get; set; }

    public string? IpAddress { get; set; }
}
