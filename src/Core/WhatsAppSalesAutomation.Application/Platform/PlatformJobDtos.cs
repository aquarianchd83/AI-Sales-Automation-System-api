using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// One tenant's copy of one recurring job, as the Platform Admin Console's Background Jobs screen shows
/// it. Merges three sources deliberately, because no one of them is the whole picture:
/// the catalog (what this job is), the <c>TenantJobSchedules</c> row (how it is configured and how the
/// tenant's last run actually went), and Hangfire's registry (whether it is really registered right now
/// and when it fires next).
///
/// <see cref="IsRegistered"/> false while <see cref="IsEnabled"/> is true is the one combination worth
/// reading closely: it means the tenant's status makes it ineligible (suspended/cancelled/deleted), or
/// a reconcile hasn't run since something changed - not that the operator disabled it.
/// </summary>
public record PlatformTenantJobDto(
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    TenantStatus TenantStatus,
    string JobType,
    string DisplayName,
    string Description,
    string CronExpression,
    string DefaultCron,
    bool IsEnabled,
    bool IsRegistered,
    DateTime? NextExecutionUtc,
    DateTime? LastExecutionUtc,
    string? HangfireLastJobState,
    DateTime? LastRunAtUtc,
    TenantJobRunOutcome? LastRunOutcome,
    string? LastRunSummary,
    int? LastRunDurationMs,
    int ConsecutiveFailureCount);

/// <summary>Every job for one tenant, for the Tenant detail screen's Background Jobs section.
/// <see cref="RunsBackgroundJobs"/> is the tenant-level answer to "why is nothing registered" - it is
/// false for a suspended/cancelled/deleted tenant regardless of any individual job's own
/// <c>IsEnabled</c>.</summary>
public record PlatformTenantJobsDto(
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    TenantStatus TenantStatus,
    bool RunsBackgroundJobs,
    IReadOnlyList<PlatformTenantJobDto> Jobs);

/// <summary>Filters for the cross-tenant Background Jobs list, on top of PagedRequest's Page/PageSize
/// (Search matches tenant name/slug).</summary>
public record PlatformJobQuery : PagedRequest
{
    public Guid? TenantId { get; init; }

    public string? JobType { get; init; }

    public bool? IsEnabled { get; init; }

    /// <summary>True narrows to jobs whose last run failed and have not succeeded since - the "what
    /// needs attention" view an operator opens this screen for.</summary>
    public bool? FailingOnly { get; init; }
}

public record UpdateTenantJobScheduleRequest
{
    public string CronExpression { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;
}

/// <summary>Result of a "Run now". <see cref="BackgroundJobId"/> is Hangfire's id for the one-off run,
/// so the operator can follow that exact execution in the Hangfire dashboard instead of guessing which
/// of the recurring runs was theirs.</summary>
public record PlatformJobTriggerResultDto(string RecurringJobId, string BackgroundJobId);

/// <summary>A recurring job that is not per-tenant - platform-global by nature (see
/// <c>TenantJobCatalog</c>'s own doc comment on why the WhatsApp token refresh is one). Read-only on
/// this screen: there is no tenant to scope a schedule edit to.</summary>
public record PlatformGlobalJobDto(
    string RecurringJobId,
    string? CronExpression,
    DateTime? NextExecutionUtc,
    DateTime? LastExecutionUtc,
    string? LastJobState,
    string? Error);
