using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// The accountability trail for everything a PlatformSuperAdmin does that reaches across or affects a
/// tenant - impersonation, suspend/reactivate/delete, plan overrides, role grants. Platform-global, not
/// <see cref="ITenantOwned"/>: an entry belongs to the platform's own history, not to whichever tenant
/// it happened to touch (and PlatformSuperAdmin actions frequently touch none, or more than one, e.g.
/// a cross-tenant user search).
///
/// <see cref="Action"/> is a free-text snapshot rather than an enum, same reasoning as
/// <c>AiInteraction.ModelUsed</c> - the set of auditable actions is expected to grow, and a stored
/// historical row should never need to change meaning (or become unrepresentable) because a later
/// release renamed or removed an enum member.
///
/// <see cref="BaseEntity.CreatedAt"/> (auto-stamped by <c>AuditableEntitySaveChangesInterceptor</c>)
/// is the entry's own timestamp - there is no separate CreatedAtUtc here.
/// </summary>
public class PlatformAuditLogEntry : BaseEntity
{
    public Guid ActorUserId { get; set; }

    /// <summary>Snapshot, not a live lookup - the acting user's email at the time, so a row stays
    /// readable even if that account is later renamed or deleted.</summary>
    public string ActorEmail { get; set; } = string.Empty;

    /// <summary>e.g. "TenantSuspended", "TenantReactivated", "TenantDeleted", "TenantImpersonated",
    /// "TenantPlanOverridden", "UserRoleGranted".</summary>
    public string Action { get; set; } = string.Empty;

    public Guid? TargetTenantId { get; set; }

    public Guid? TargetUserId { get; set; }

    /// <summary>Free-text/JSON detail specific to the action - e.g. the old/new plan for a plan
    /// override, or the impersonated user's email for an impersonation entry.</summary>
    public string? Details { get; set; }
}
