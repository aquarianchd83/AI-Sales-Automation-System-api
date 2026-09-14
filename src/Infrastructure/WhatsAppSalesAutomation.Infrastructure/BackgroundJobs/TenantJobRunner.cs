using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// The shared body of every per-tenant recurring job: establish the tenant, run the work, record how it
/// went. Each of CampaignInitialSenderJob/FollowUpSchedulerJob/MessageStatusRetryJob/
/// MessageTemplateSyncJob is now a thin wrapper around one call to this.
///
/// This replaces the fan-out runner of the same name, which listed every active tenant and looped them
/// inside a single global job execution. The loop is gone because the scheduler is what iterates
/// tenants now - one Hangfire recurring job per tenant per job type - which is what makes a tenant's
/// schedule individually configurable, individually pausable, and individually visible in both the
/// Hangfire dashboard and the Platform Admin Console. It also means one tenant's slow run no longer
/// delays every tenant behind it in the loop, and one tenant's failure is a failure of that tenant's own
/// job rather than something that had to be swallowed to protect the others.
///
/// A fresh <see cref="IServiceScope"/> is still created per run rather than using the job class's own
/// injected services: <c>ApplicationDbContext</c>/<c>ITenantContext</c> are Scoped, and the tenant has
/// to be set on the context <i>before</i> anything tenant-aware (the send service, the WhatsApp/AI
/// factories) is resolved from it.
/// </summary>
public class TenantJobRunner
{
    private const int MaxSummaryLength = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _dateTime;
    private readonly ILogger<TenantJobRunner> _logger;

    public TenantJobRunner(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider dateTime,
        ILogger<TenantJobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _dateTime = dateTime;
        _logger = logger;
    }

    /// <param name="runForTenantAsync">The job's actual work, given a tenant-scoped service provider.
    /// Returns the one-line summary stored on the schedule row (counters, typically) - null for "nothing
    /// worth reporting".</param>
    public async Task RunAsync(
        Guid tenantId,
        string jobType,
        Func<IServiceProvider, CancellationToken, Task<string?>> runForTenantAsync,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<IApplicationDbContext>();

        var skipReason = await GetSkipReasonAsync(context, tenantId, jobType, cancellationToken);
        if (skipReason is not null)
        {
            // The registration should already have been removed when the tenant was suspended or the job
            // disabled, so reaching here means something is out of step - most likely a run that was
            // already queued when that happened. Recorded rather than silently dropped, so the console
            // shows why the last run did nothing, and left for the next reconcile to actually fix.
            _logger.LogInformation("{JobType} skipped for tenant {TenantId}: {Reason}", jobType, tenantId, skipReason);
            await RecordAsync(tenantId, jobType, TenantJobRunOutcome.Skipped, skipReason, durationMs: 0, cancellationToken);
            return;
        }

        services.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var summary = await runForTenantAsync(services, cancellationToken);
            stopwatch.Stop();
            await RecordAsync(tenantId, jobType, TenantJobRunOutcome.Succeeded, summary, (int)stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "{JobType} failed for tenant {TenantId}", jobType, tenantId);
            await RecordAsync(tenantId, jobType, TenantJobRunOutcome.Failed, ex.Message, (int)stopwatch.ElapsedMilliseconds, cancellationToken);

            // Deliberately swallowed, as it was under the fan-out runner, but for a different reason now
            // that a run only concerns one tenant: rethrowing would put the job into Hangfire's retry
            // pipeline, which for a minutely job means piling retries on top of the next scheduled run.
            // The failure is recorded on that tenant's own schedule row (and its ConsecutiveFailureCount)
            // for the console to surface instead.
        }
    }

    /// <summary>Why this run should do nothing, or null to go ahead. A missing schedule row is not a skip
    /// reason - the row is bookkeeping, and a job that is registered without one should still run.</summary>
    private static async Task<string?> GetSkipReasonAsync(
        IApplicationDbContext context,
        Guid tenantId,
        string jobType,
        CancellationToken cancellationToken)
    {
        var status = await context.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => (TenantStatus?)t.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is null)
            return "tenant no longer exists";

        if (!TenantStatusRules.AllowsBackgroundJobs(status.Value))
            return "tenant is " + status.Value;

        var isEnabled = await context.TenantJobSchedules
            .Where(s => s.TenantId == tenantId && s.JobType == jobType)
            .Select(s => (bool?)s.IsEnabled)
            .FirstOrDefaultAsync(cancellationToken);

        return isEnabled == false ? "job is disabled for this tenant" : null;
    }

    /// <summary>Writes the outcome back to the schedule row in its own scope - never the scope the job
    /// just ran in, whose DbContext is both tenant-scoped and still tracking every entity that run
    /// touched. Failing to record must never turn a successful run into a failed one, so this swallows
    /// its own errors after logging them.</summary>
    private async Task RecordAsync(
        Guid tenantId,
        string jobType,
        TenantJobRunOutcome outcome,
        string? summary,
        int durationMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

            var schedule = await context.TenantJobSchedules
                .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.JobType == jobType, cancellationToken);

            if (schedule is null)
                return;

            schedule.LastRunAtUtc = _dateTime.UtcNow;
            schedule.LastRunOutcome = outcome;
            schedule.LastRunSummary = Truncate(summary);
            schedule.LastRunDurationMs = durationMs;
            schedule.ConsecutiveFailureCount = outcome switch
            {
                TenantJobRunOutcome.Failed => schedule.ConsecutiveFailureCount + 1,
                TenantJobRunOutcome.Succeeded => 0,
                // A skip is neither a success nor a failure, so it leaves an existing failure streak
                // alone rather than clearing one behind a suspension.
                _ => schedule.ConsecutiveFailureCount
            };

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the {JobType} run outcome for tenant {TenantId}", jobType, tenantId);
        }
    }

    private static string? Truncate(string? summary)
        => summary is not null && summary.Length > MaxSummaryLength ? summary[..MaxSummaryLength] : summary;
}
