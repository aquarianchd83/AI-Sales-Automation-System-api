using Cronos;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// The one place <c>TenantJobTypes</c>' string keys are turned into actual Hangfire registrations
/// against actual job classes. That mapping is why <see cref="ITenantJobScheduler"/> is an abstraction
/// at all rather than the Application layer calling Hangfire itself: the job classes live here, so no
/// Application-layer code could express <c>AddOrUpdate&lt;CampaignInitialSenderJob&gt;</c> even if it
/// were willing to reference Hangfire.
///
/// Registered as a singleton: every dependency it has (<see cref="IRecurringJobManager"/>,
/// <see cref="IBackgroundJobClient"/>, <see cref="JobStorage"/>) is one itself, and it holds no
/// per-request state.
/// </summary>
public class HangfireTenantJobScheduler : ITenantJobScheduler
{
    private readonly IRecurringJobManager _recurringJobs;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly JobStorage _storage;
    private readonly ILogger<HangfireTenantJobScheduler> _logger;

    public HangfireTenantJobScheduler(
        IRecurringJobManager recurringJobs,
        IBackgroundJobClient backgroundJobs,
        JobStorage storage,
        ILogger<HangfireTenantJobScheduler> logger)
    {
        _recurringJobs = recurringJobs;
        _backgroundJobs = backgroundJobs;
        _storage = storage;
        _logger = logger;
    }

    public bool IsValidCron(string cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return false;

        try
        {
            // Mirrors how Hangfire itself parses a recurring job's cron (Cronos is its own parser): six
            // fields means the optional leading seconds field is present, five is the standard form.
            // Parsing here with the same rules is the whole point - an expression this accepts is one
            // Hangfire will accept too.
            var fields = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var format = fields.Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
            CronExpression.Parse(cronExpression.Trim(), format);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    public void AddOrUpdateTenantJob(Guid tenantId, string jobType, string cronExpression)
    {
        var recurringJobId = TenantJobCatalog.RecurringJobId(jobType, tenantId);

        // Deliberately scheduled in UTC (Hangfire's default) rather than the tenant's own timezone: the
        // per-tenant jobs are frequent polls (minutely/5-minutely/hourly) plus a daily token refresh whose
        // 10-day window makes the hour it runs irrelevant, and "is this campaign step due yet" is already
        // evaluated in the tenant's timezone
        // inside the send service via ITenantTimeZoneProvider. A future job with a genuine time-of-day
        // (a daily digest, say) is where RecurringJobOptions.TimeZone would need to come from the tenant.
        switch (jobType)
        {
            case TenantJobTypes.CampaignInitialSends:
                _recurringJobs.AddOrUpdate<CampaignInitialSenderJob>(recurringJobId, job => job.RunAsync(tenantId), cronExpression);
                break;
            case TenantJobTypes.CampaignFollowUps:
                _recurringJobs.AddOrUpdate<FollowUpSchedulerJob>(recurringJobId, job => job.RunAsync(tenantId), cronExpression);
                break;
            case TenantJobTypes.CampaignSendRetries:
                _recurringJobs.AddOrUpdate<MessageStatusRetryJob>(recurringJobId, job => job.RunAsync(tenantId), cronExpression);
                break;
            case TenantJobTypes.WhatsAppTemplateSync:
                _recurringJobs.AddOrUpdate<MessageTemplateSyncJob>(recurringJobId, job => job.RunAsync(tenantId), cronExpression);
                break;
            case TenantJobTypes.WhatsAppTokenRefresh:
                _recurringJobs.AddOrUpdate<WhatsAppTokenRefreshJob>(recurringJobId, job => job.RunAsync(tenantId), cronExpression);
                break;
            default:
                // Reachable only from a schedule row for a job type this build no longer knows about -
                // the provisioner already skips those, so this is the belt-and-braces half of that guard.
                _logger.LogWarning("Ignoring unknown per-tenant job type {JobType} for tenant {TenantId}", jobType, tenantId);
                break;
        }
    }

    public void RemoveTenantJob(Guid tenantId, string jobType)
        => _recurringJobs.RemoveIfExists(TenantJobCatalog.RecurringJobId(jobType, tenantId));

    public string TriggerTenantJob(Guid tenantId, string jobType)
    {
        // Enqueued directly rather than via IRecurringJobManager.Trigger: Trigger returns nothing, and
        // the operator needs the resulting background job id to find their own run in the dashboard.
        // Either way the run is an ordinary execution of the same method, so [DisableConcurrentExecution]
        // still keeps it from overlapping the scheduled one.
        return jobType switch
        {
            TenantJobTypes.CampaignInitialSends => _backgroundJobs.Enqueue<CampaignInitialSenderJob>(job => job.RunAsync(tenantId)),
            TenantJobTypes.CampaignFollowUps => _backgroundJobs.Enqueue<FollowUpSchedulerJob>(job => job.RunAsync(tenantId)),
            TenantJobTypes.CampaignSendRetries => _backgroundJobs.Enqueue<MessageStatusRetryJob>(job => job.RunAsync(tenantId)),
            TenantJobTypes.WhatsAppTemplateSync => _backgroundJobs.Enqueue<MessageTemplateSyncJob>(job => job.RunAsync(tenantId)),
            TenantJobTypes.WhatsAppTokenRefresh => _backgroundJobs.Enqueue<WhatsAppTokenRefreshJob>(job => job.RunAsync(tenantId)),
            _ => throw new ArgumentOutOfRangeException(nameof(jobType), jobType, "Not a per-tenant background job.")
        };
    }

    public IReadOnlyDictionary<string, RecurringJobRegistrationState> GetRegistrations(IEnumerable<string> recurringJobIds)
    {
        var ids = recurringJobIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<string, RecurringJobRegistrationState>();

        using var connection = _storage.GetConnection();

        // Removed entries must be filtered out, not assumed absent: asked for specific ids, Hangfire
        // returns a tombstone DTO (Removed = true, no schedule) for an id that is not registered rather
        // than omitting it - so without this, a job that was just paused would still report itself as
        // registered on the console, with only its empty next-run time hinting otherwise.
        return connection.GetRecurringJobs(ids)
            .Where(job => job is not null && !job.Removed)
            .ToDictionary(job => job.Id, ToState);
    }

    public IReadOnlyList<string> GetRegisteredTenantJobIds()
    {
        using var connection = _storage.GetConnection();

        return connection.GetRecurringJobs()
            .Select(job => job.Id)
            .Where(IsTenantJobId)
            .ToList();
    }

    public IReadOnlyList<RecurringJobRegistrationState> GetPlatformJobs()
    {
        using var connection = _storage.GetConnection();

        return connection.GetRecurringJobs()
            .Where(job => !IsTenantJobId(job.Id))
            .OrderBy(job => job.Id)
            .Select(ToState)
            .ToList();
    }

    /// <summary>Whether this recurring job id is one of ours, i.e. <c>{known job type}:{guid}</c>. Both
    /// halves are checked rather than just splitting on the colon, so an unrelated recurring job that
    /// happens to contain one is never mistaken for a tenant job and unregistered as an orphan.</summary>
    private static bool IsTenantJobId(string recurringJobId)
    {
        var separator = recurringJobId.LastIndexOf(':');

        return separator > 0
               && TenantJobCatalog.IsKnown(recurringJobId[..separator])
               && Guid.TryParse(recurringJobId[(separator + 1)..], out _);
    }

    private static RecurringJobRegistrationState ToState(RecurringJobDto job)
        => new(job.Id, job.Cron, job.NextExecution, job.LastExecution, job.LastJobState, job.Error);
}
