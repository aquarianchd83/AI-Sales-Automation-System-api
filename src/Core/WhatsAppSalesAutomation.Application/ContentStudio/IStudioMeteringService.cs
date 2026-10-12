using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

/// <summary>
/// The gate every metered Content Studio step passes through, and the tenant's record of it. Charge spends quota
/// through the shared quota ledger (never a parallel balance) and writes the activity row the tenant sees; the caller
/// runs the work, then closes the activity with Complete or - when our side or the provider's failed - Refund, which
/// returns the units. Every method is safe to retry with the same operation key.
/// </summary>
public interface IStudioMeteringService
{
    Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default);

    /// <summary>Gives the units back and marks the activity refunded with <paramref name="reason"/>. Idempotent.</summary>
    Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default);

    /// <summary>The tenant's activity, newest first.</summary>
    Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default);

    Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
