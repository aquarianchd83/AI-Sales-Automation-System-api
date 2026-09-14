using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <inheritdoc />
public class PlatformJobService : IPlatformJobService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantJobScheduler _scheduler;
    private readonly ITenantJobProvisioner _provisioner;
    private readonly IValidator<UpdateTenantJobScheduleRequest> _updateValidator;
    private readonly IPlatformAuditService _auditService;

    public PlatformJobService(
        IApplicationDbContext context,
        ITenantJobScheduler scheduler,
        ITenantJobProvisioner provisioner,
        IValidator<UpdateTenantJobScheduleRequest> updateValidator,
        IPlatformAuditService auditService)
    {
        _context = context;
        _scheduler = scheduler;
        _provisioner = provisioner;
        _updateValidator = updateValidator;
        _auditService = auditService;
    }

    public async Task<PagedResult<PlatformTenantJobDto>> GetPagedAsync(PlatformJobQuery query, CancellationToken cancellationToken = default)
    {
        // Filtered and sorted against the entities themselves, projecting only at the end: the tenant is
        // joined in (rather than looked up per row) because this screen's default view is every tenant's
        // every job, which would otherwise be one round trip per row.
        var schedules = _context.TenantJobSchedules.AsQueryable();

        if (query.TenantId is { } tenantId)
            schedules = schedules.Where(s => s.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.JobType))
            schedules = schedules.Where(s => s.JobType == query.JobType);

        if (query.IsEnabled is { } isEnabled)
            schedules = schedules.Where(s => s.IsEnabled == isEnabled);

        if (query.FailingOnly == true)
            schedules = schedules.Where(s => s.ConsecutiveFailureCount > 0);

        var rows = from schedule in schedules
                   join tenant in _context.Tenants on schedule.TenantId equals tenant.Id
                   select new { Schedule = schedule, Tenant = tenant };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            rows = rows.Where(r => r.Tenant.Name.Contains(search) || r.Tenant.Slug.Contains(search));
        }

        var totalCount = await rows.CountAsync(cancellationToken);

        var page = await rows
            // Failing first, then by tenant, then by job type - an operator opens this screen looking
            // for what is broken, not for alphabetical order.
            .OrderByDescending(r => r.Schedule.ConsecutiveFailureCount)
            .ThenBy(r => r.Tenant.Name)
            .ThenBy(r => r.Schedule.JobType)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var materialized = page.Select(r => new ScheduleRow(r.Schedule, r.Tenant)).ToList();
        var registrations = GetRegistrationsFor(materialized);

        return new PagedResult<PlatformTenantJobDto>(
            materialized.Select(r => ToDto(r.Schedule, r.Tenant, registrations)).ToList(),
            totalCount,
            query.Page,
            query.PageSize);
    }

    public async Task<PlatformTenantJobsDto> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);

        // Provision first, so a tenant created before this feature existed (or one whose rows were
        // removed) shows its jobs the first time an operator opens the screen, instead of an empty list
        // that only fills in after the next reconcile.
        await _provisioner.SyncTenantAsync(tenantId, cancellationToken);

        var schedules = await _context.TenantJobSchedules
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var rows = OrderByCatalog(schedules).Select(s => new ScheduleRow(s, tenant)).ToList();
        var registrations = GetRegistrationsFor(rows);

        return new PlatformTenantJobsDto(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Status,
            TenantStatusRules.AllowsBackgroundJobs(tenant.Status),
            rows.Select(r => ToDto(r.Schedule, tenant, registrations)).ToList());
    }

    public async Task<PlatformTenantJobDto> UpdateScheduleAsync(
        Guid tenantId,
        string jobType,
        UpdateTenantJobScheduleRequest request,
        Guid actorUserId,
        string actorEmail,
        CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        var schedule = await GetScheduleOrThrowAsync(tenantId, jobType, cancellationToken);

        var previousCron = schedule.CronExpression;
        var previousEnabled = schedule.IsEnabled;

        schedule.CronExpression = request.CronExpression.Trim();
        schedule.IsEnabled = request.IsEnabled;
        await _context.SaveChangesAsync(cancellationToken);

        // The table is the source of truth, so it is saved first and Hangfire brought in line after,
        // never the other way round. A failure between the two is corrected by the next reconcile.
        await _provisioner.SyncTenantAsync(tenantId, cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantJobScheduleUpdated, tenantId,
            details: $"{jobType}: cron {previousCron} -> {schedule.CronExpression}, enabled {previousEnabled} -> {schedule.IsEnabled}",
            cancellationToken: cancellationToken);

        var registrations = GetRegistrationsFor(new[] { new ScheduleRow(schedule, tenant) });
        return ToDto(schedule, tenant, registrations);
    }

    public async Task<PlatformJobTriggerResultDto> TriggerAsync(
        Guid tenantId,
        string jobType,
        Guid actorUserId,
        string actorEmail,
        CancellationToken cancellationToken = default)
    {
        var tenant = await GetTenantOrThrowAsync(tenantId, cancellationToken);
        var schedule = await GetScheduleOrThrowAsync(tenantId, jobType, cancellationToken);

        if (!TenantStatusRules.AllowsBackgroundJobs(tenant.Status))
            throw new ConflictException($"Tenant {tenant.Name} is {tenant.Status} - its background jobs cannot be run.");

        if (!schedule.IsEnabled)
            throw new ConflictException($"Job {jobType} is disabled for tenant {tenant.Name} - enable it before running it.");

        var backgroundJobId = _scheduler.TriggerTenantJob(tenantId, jobType);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantJobTriggered, tenantId,
            details: $"{jobType} (Hangfire job {backgroundJobId})", cancellationToken: cancellationToken);

        return new PlatformJobTriggerResultDto(TenantJobCatalog.RecurringJobId(jobType, tenantId), backgroundJobId);
    }

    public Task<IReadOnlyList<PlatformGlobalJobDto>> GetPlatformJobsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PlatformGlobalJobDto> jobs = _scheduler.GetPlatformJobs()
            .Select(j => new PlatformGlobalJobDto(j.RecurringJobId, j.Cron, j.NextExecutionUtc, j.LastExecutionUtc, j.LastJobState, j.Error))
            .ToList();

        return Task.FromResult(jobs);
    }

    public async Task<TenantJobReconcileSummary> ReconcileAsync(Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var summary = await _provisioner.ReconcileAllAsync(cancellationToken);

        await _auditService.LogAsync(
            actorUserId, actorEmail, PlatformAuditActions.TenantJobsReconciled,
            details: $"tenants={summary.TenantsExamined} schedulesCreated={summary.SchedulesCreated} " +
                     $"registered={summary.JobsRegistered} removed={summary.JobsRemoved} orphansRemoved={summary.OrphanRegistrationsRemoved}",
            cancellationToken: cancellationToken);

        return summary;
    }

    private async Task<Tenant> GetTenantOrThrowAsync(Guid tenantId, CancellationToken cancellationToken)
        => await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
           ?? throw new NotFoundException(nameof(Tenant), tenantId);

    private async Task<TenantJobSchedule> GetScheduleOrThrowAsync(Guid tenantId, string jobType, CancellationToken cancellationToken)
    {
        if (!TenantJobCatalog.IsKnown(jobType))
            throw new NotFoundException($"{jobType} is not a per-tenant background job.");

        // A tenant predating this feature has no rows until something provisions them, and an operator
        // reaching this straight from a deep link would otherwise get a 404 for a job that genuinely
        // exists - so create the missing rows rather than reporting them absent.
        await _provisioner.SyncTenantAsync(tenantId, cancellationToken);

        return await _context.TenantJobSchedules.FirstOrDefaultAsync(
                   s => s.TenantId == tenantId && s.JobType == jobType, cancellationToken)
               ?? throw new NotFoundException($"Job {jobType} was not found for tenant {tenantId}.");
    }

    /// <summary>Hangfire's registry is read once per request for exactly the rows being returned, not per
    /// row - <see cref="ITenantJobScheduler.GetRegistrations"/> is a storage round trip.</summary>
    private IReadOnlyDictionary<string, RecurringJobRegistrationState> GetRegistrationsFor(IEnumerable<ScheduleRow> rows)
        => _scheduler.GetRegistrations(
            rows.Select(r => TenantJobCatalog.RecurringJobId(r.Schedule.JobType, r.Schedule.TenantId)));

    /// <summary>Catalog order, not alphabetical: the four campaign/template jobs read as a pipeline
    /// (initial sends, follow-ups, retries, template sync) and an operator scanning one tenant's jobs is
    /// reading that pipeline. A row for a job type no longer in the catalog sorts last rather than
    /// being dropped.</summary>
    private static IEnumerable<TenantJobSchedule> OrderByCatalog(IEnumerable<TenantJobSchedule> schedules)
    {
        var order = TenantJobCatalog.All
            .Select((definition, index) => (definition.Key, index))
            .ToDictionary(x => x.Key, x => x.index);

        return schedules
            .OrderBy(s => order.TryGetValue(s.JobType, out var index) ? index : int.MaxValue)
            .ThenBy(s => s.JobType);
    }

    private static PlatformTenantJobDto ToDto(
        TenantJobSchedule schedule,
        Tenant tenant,
        IReadOnlyDictionary<string, RecurringJobRegistrationState> registrations)
    {
        var definition = TenantJobCatalog.Find(schedule.JobType);
        var recurringJobId = TenantJobCatalog.RecurringJobId(schedule.JobType, schedule.TenantId);
        registrations.TryGetValue(recurringJobId, out var registration);

        return new PlatformTenantJobDto(
            schedule.TenantId,
            tenant.Name,
            tenant.Slug,
            tenant.Status,
            schedule.JobType,
            definition?.DisplayName ?? schedule.JobType,
            definition?.Description ?? string.Empty,
            schedule.CronExpression,
            definition?.DefaultCron ?? schedule.CronExpression,
            schedule.IsEnabled,
            registration is not null,
            registration?.NextExecutionUtc,
            registration?.LastExecutionUtc,
            registration?.LastJobState,
            AsUtc(schedule.LastRunAtUtc),
            schedule.LastRunOutcome,
            schedule.LastRunSummary,
            schedule.LastRunDurationMs,
            schedule.ConsecutiveFailureCount);
    }

    /// <summary>
    /// Marks a timestamp read back from the database as UTC, which is what it is: LastRunAtUtc is
    /// written from IDateTimeProvider.UtcNow, but SQL Server's datetime2 carries no offset, so EF
    /// returns it with DateTimeKind.Unspecified - and System.Text.Json then serializes it with no
    /// trailing "Z". A browser reads that as local time, which put the console's "last run" hours away
    /// from the "next run" beside it (that one comes from Hangfire, already marked UTC). Stamping the
    /// Kind here is what makes the two comparable.
    /// </summary>
    private static DateTime? AsUtc(DateTime? value)
        => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    /// <summary>A schedule row with the tenant it belongs to - the shape both the paged query and the
    /// single-tenant read project into so they can share <see cref="ToDto"/>.</summary>
    private record ScheduleRow(TenantJobSchedule Schedule, Tenant Tenant);
}
