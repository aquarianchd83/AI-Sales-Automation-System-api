using Hangfire;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Registers the campaign pipeline's three recurring jobs. Called once from Program.cs
/// after the app is built - Hangfire persists the schedule in SQL Server, so this is idempotent
/// across restarts (re-registering the same id with the same cron is a no-op).</summary>
public static class RecurringJobsRegistrar
{
    public static void RegisterAll(IRecurringJobManager recurringJobs)
    {
        recurringJobs.AddOrUpdate<CampaignInitialSenderJob>(
            "campaign-initial-sends", job => job.RunAsync(), Cron.Minutely());

        recurringJobs.AddOrUpdate<FollowUpSchedulerJob>(
            "campaign-follow-ups", job => job.RunAsync(), Cron.Minutely());

        recurringJobs.AddOrUpdate<MessageStatusRetryJob>(
            "campaign-send-retries", job => job.RunAsync(), "*/5 * * * *");
    }
}
