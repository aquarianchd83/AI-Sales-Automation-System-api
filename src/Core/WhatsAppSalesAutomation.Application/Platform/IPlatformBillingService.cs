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
    Task<IReadOnlyList<PlatformPlanDto>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<PagedResult<PlatformSubscriptionListItemDto>> GetSubscriptionsAsync(PlatformSubscriptionQuery query, CancellationToken cancellationToken = default);
}
