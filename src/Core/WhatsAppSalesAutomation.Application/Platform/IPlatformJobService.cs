using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// The Platform Admin Console's Background Jobs screen - PlatformSuperAdmin-only, like every other
/// Platform* service. This is the only way a schedule is ever changed: it validates the cron, persists
/// the <c>TenantJobSchedules</c> row, re-syncs Hangfire through <see cref="ITenantJobProvisioner"/> and
/// audits the change as one operation, so the table, the scheduler and the audit trail cannot disagree.
///
/// A tenant's own Admin deliberately gets nothing here - background job scheduling is platform
/// operations, the same call that was made for WhatsApp/AI credentials.
/// </summary>
public interface IPlatformJobService
{
    Task<PagedResult<PlatformTenantJobDto>> GetPagedAsync(PlatformJobQuery query, CancellationToken cancellationToken = default);

    Task<PlatformTenantJobsDto> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<PlatformTenantJobDto> UpdateScheduleAsync(
        Guid tenantId,
        string jobType,
        UpdateTenantJobScheduleRequest request,
        Guid actorUserId,
        string actorEmail,
        CancellationToken cancellationToken = default);

    /// <summary>Fires one tenant's copy of a job immediately without touching its schedule. Refused for
    /// a disabled job or an ineligible (suspended/cancelled/deleted) tenant - a "run now" that sent that
    /// tenant's campaigns anyway would be a way around the pause it is meant to be under.</summary>
    Task<PlatformJobTriggerResultDto> TriggerAsync(
        Guid tenantId,
        string jobType,
        Guid actorUserId,
        string actorEmail,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlatformGlobalJobDto>> GetPlatformJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Operator-triggered version of the reconcile pass that also runs at startup and hourly -
    /// the "it looks wrong, put it back" button, so a support case never needs an app restart.</summary>
    Task<TenantJobReconcileSummary> ReconcileAsync(Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}
