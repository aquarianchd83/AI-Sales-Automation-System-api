using Hangfire;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// The options every recurring job in this application is registered with. One place, so a new job gets the
/// same treatment by default instead of each registration deciding for itself.
/// </summary>
public static class RecurringJobPolicy
{
    /// <summary>
    /// Pass this to every <c>AddOrUpdate</c>, for per-tenant jobs (see <see cref="HangfireTenantJobScheduler"/>)
    /// and platform-wide ones (see <see cref="RecurringJobsRegistrar"/>) alike.
    ///
    /// Hangfire's default is <see cref="MisfireHandlingMode.Relaxed"/>: an occurrence that came due while the
    /// process was stopped is enqueued as soon as it comes back. That produces a burst of catch-up runs at
    /// every startup, hours outside the scheduled window - and for a job that spends money per run (lead
    /// discovery researches the web on a paid model) it is a charge nobody asked for, on every deploy,
    /// restart, or morning after a machine was left off.
    ///
    /// <see cref="MisfireHandlingMode.Ignorable"/> skips the missed occurrence and waits for the next
    /// scheduled one. Nothing is lost, because every recurring job here polls for work that is due rather
    /// than performing a one-off action tied to its instant: a campaign send skipped at 09:01 is still due at
    /// 09:02, a template sync at 11:00 still has the same templates to sync at 12:00, and a token refresh only
    /// acts inside a ten-day window. A job that must not miss its exact moment (a report for a specific day,
    /// say) would need its own options and its own catch-up reasoning - it should not silently inherit this.
    ///
    /// An operator's "Run now" is unaffected: that enqueues the job directly rather than through the
    /// recurring schedule.
    /// </summary>
    public static readonly RecurringJobOptions SkipMissedOccurrences = new()
    {
        MisfireHandling = MisfireHandlingMode.Ignorable
    };
}
