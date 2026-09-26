namespace WhatsAppSalesAutomation.Application.Platform;

public static class TenantJobMappings
{
    public static TenantJobDto ToTenantDto(this PlatformTenantJobDto dto) => new(
        dto.JobType,
        dto.DisplayName,
        dto.Description,
        dto.CronExpression,
        dto.DefaultCron,
        dto.IsEnabled,
        dto.IsRegistered,
        dto.NextExecutionUtc,
        dto.LastExecutionUtc,
        dto.HangfireLastJobState,
        dto.LastRunAtUtc,
        dto.LastRunOutcome,
        dto.LastRunSummary,
        dto.LastRunDurationMs,
        dto.ConsecutiveFailureCount);

    /// <summary>Also narrows to the self-service job types - the platform DTO carries every job type
    /// for the tenant, including the integration-plumbing ones a tenant Admin is not allowed to see or
    /// change here.</summary>
    public static TenantJobsDto ToTenantDto(this PlatformTenantJobsDto dto, IReadOnlySet<string> visibleJobTypes) =>
        new(
            dto.RunsBackgroundJobs,
            dto.Jobs.Where(j => visibleJobTypes.Contains(j.JobType)).Select(j => j.ToTenantDto()).ToList());
}
