using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// One of the calling tenant's own recurring jobs, as its own Admin is allowed to see and change it -
/// <see cref="PlatformTenantJobDto"/> with every platform-identity field (TenantId/TenantName/TenantSlug/
/// TenantStatus) trimmed off, since a tenant already knows who it is. See
/// <see cref="TenantJobCatalog.SelfServiceKeys"/> for which job types a tenant can reach this way -
/// background jobs that are platform/integration plumbing (WhatsApp template sync, token refresh) stay
/// operator-only.
/// </summary>
public record TenantJobDto(
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

/// <summary>The calling tenant's self-service jobs. <see cref="RunsBackgroundJobs"/> is false only when
/// the tenant's own account status (suspended/cancelled) makes every job ineligible, regardless of any
/// individual job's <see cref="TenantJobDto.IsEnabled"/> - see TenantStatusRules.</summary>
public record TenantJobsDto(bool RunsBackgroundJobs, IReadOnlyList<TenantJobDto> Jobs);
