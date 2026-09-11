using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Platform Admin Console's Subscriptions & Billing screen (spec item #2 of the two billing halves -
/// the plan catalog and the per-tenant subscription list; the third, plan override, lives on
/// <see cref="IPlatformTenantService.OverridePlanAsync"/> since it acts on one tenant, the same seam
/// Suspend/Reactivate/Delete/Impersonate already use).
/// </summary>
public interface IPlatformBillingService
{
    /// <summary>Every plan, active and retired - unlike the tenant-facing IBillingService.GetPlansAsync,
    /// which only ever returns active ones.</summary>
    Task<IReadOnlyList<PlatformPlanDto>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<PagedResult<PlatformSubscriptionListItemDto>> GetSubscriptionsAsync(PlatformSubscriptionQuery query, CancellationToken cancellationToken = default);

    Task<PlatformPlanDto> CreatePlanAsync(CreatePlanRequest request, CancellationToken cancellationToken = default);

    Task<PlatformPlanDto> UpdatePlanAsync(Guid id, UpdatePlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retires the plan (IsActive = false) - never a hard delete, see Plan.IsActive's own
    /// doc comment for why (existing Subscriptions keep referencing it). A no-op, not an error, if
    /// the plan is already retired.</summary>
    Task DeactivatePlanAsync(Guid id, CancellationToken cancellationToken = default);
}
