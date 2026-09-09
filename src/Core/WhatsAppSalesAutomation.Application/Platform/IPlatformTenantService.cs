using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Platform Admin Console's Tenants screen (spec item #2) - list/search every organization, view a
/// detail roll-up, and the destructive/support actions a PlatformSuperAdmin can take on one. Every
/// mutating method here is audited via <see cref="IPlatformAuditService"/> as part of the same call.
/// </summary>
public interface IPlatformTenantService
{
    Task<PagedResult<PlatformTenantListItemDto>> GetPagedAsync(PlatformTenantQuery query, CancellationToken cancellationToken = default);

    Task<PlatformTenantDetailDto> GetDetailAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Locks the tenant out (login already refuses Suspended tenants - see
    /// AuthService.LoginAsync) without touching any of its data.</summary>
    Task SuspendAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Reverses Suspend, or un-deletes a tenant that was previously soft-deleted - both land
    /// back on <see cref="Domain.Enums.TenantStatus.Active"/>.</summary>
    Task ReactivateAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Terminal status change only (see <see cref="Domain.Enums.TenantStatus.Deleted"/>'s own
    /// doc comment) - never a physical cascade-delete of the tenant's data.</summary>
    Task DeleteAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Issues a short-lived, no-refresh-token access token for the tenant's own admin (see
    /// <c>IJwtTokenService.GenerateImpersonationAccessToken</c> and the returned
    /// <c>impersonated_by</c> claim), for a support session. Picks <c>Tenant.OwnerUserId</c> when it
    /// is still an active user, otherwise the tenant's longest-standing active Admin/SuperAdmin user.</summary>
    Task<ImpersonationSessionDto> ImpersonateAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    /// <summary>Force-sets <c>Subscription.PlanId</c> without Stripe - see
    /// <see cref="OverrideTenantPlanRequest"/>'s own doc comment.</summary>
    Task OverridePlanAsync(Guid tenantId, OverrideTenantPlanRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}
