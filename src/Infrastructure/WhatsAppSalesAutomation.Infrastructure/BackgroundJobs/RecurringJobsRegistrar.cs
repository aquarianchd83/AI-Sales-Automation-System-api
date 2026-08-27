using Hangfire;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Registers every recurring job - the campaign pipeline's three, plus the WhatsApp token
/// refresh check. Called once from Program.cs after the app is built - Hangfire persists the schedule
/// in SQL Server, so this is idempotent across restarts (re-registering the same id with the same
/// cron is a no-op).</summary>
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

        recurringJobs.AddOrUpdate<WhatsAppTokenRefreshJob>(
            "whatsapp-token-refresh", job => job.RunAsync(), Cron.Daily());
    }
}
