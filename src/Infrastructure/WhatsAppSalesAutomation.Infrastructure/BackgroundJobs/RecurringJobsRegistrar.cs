using Hangfire;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the recurring jobs that are genuinely platform-global, and clears out the global
/// registrations that the per-tenant jobs replaced. Called once from Program.cs after the app is built,
/// immediately before the first <c>ITenantJobProvisioner.ReconcileAllAsync</c> pass, which is what
/// registers the per-tenant jobs themselves. Hangfire persists schedules in SQL Server, so this is
/// idempotent across restarts.
///
/// Every per-tenant job is deliberately absent - campaign sends, follow-ups, retries, template sync and
/// the WhatsApp token refresh are all registered per tenant as <c>{jobType}:{tenantId}</c> from each
/// tenant's own <c>TenantJobSchedules</c> row. See <see cref="TenantJobCatalog"/>.
/// </summary>
public static class RecurringJobsRegistrar
{
    public static void RegisterAll(IRecurringJobManager recurringJobs)
    {
        RemoveLegacyGlobalTenantJobs(recurringJobs);

        // Daily, not hourly: nothing normal depends on this pass - every path that changes a tenant's
        // status or schedule syncs Hangfire inline - so it only exists to catch drift, and drift is rare
        // enough that an operator noticing it and pressing "Reconcile now" is the expected trigger. A
        // scheduled pass this infrequent is the backstop for when nobody is looking, not the mechanism.
        // 00:30 UTC keeps it clear of the per-tenant token refreshes at midnight and the template syncs
        // at :00.
        recurringJobs.AddOrUpdate<TenantJobReconciliationJob>(
            "tenant-job-reconciliation", job => job.RunAsync(), "30 0 * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Hourly: renews subscriptions whose paid period has ended and writes off lapsed quota. Idempotent,
        // so an overlapping or repeated run is harmless.
        recurringJobs.AddOrUpdate<SubscriptionMaintenanceJob>(
            "subscription-maintenance", job => job.RunAsync(), "5 * * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Every 5 minutes: applies stored Razorpay subscription webhooks that no job picked up or that failed and are due another try
        // (the wait between tries grows, so this is cheap when there is nothing to do). A no-op until AutoPay has mandates.
        recurringJobs.AddOrUpdate<AutoPayMaintenanceJob>(
            "razorpay-webhook-retry", job => job.RetryWebhooksAsync(), "*/5 * * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Every 5 minutes: settles wallet top-ups whose browser was closed or whose webhook never arrived, by asking Razorpay about each pending order.
        recurringJobs.AddOrUpdate<WalletRechargeReconciliationJob>(
            "wallet-recharge-reconciliation", job => job.RunAsync(), "*/5 * * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Daily at 01:45 UTC, clear of the passes at 00:30 and 03:20: compares every live AutoPay mandate with Razorpay's own record, so a
        // webhook that never arrived cannot leave a mandate wrong for more than a day. Read-mostly at Razorpay; it never starts a debit.
        recurringJobs.AddOrUpdate<AutoPayMaintenanceJob>(
            "autopay-reconciliation", job => job.ReconcileAsync(), "45 1 * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Hourly: a plan whose mandate has stopped paying (halted, paused, cancelled) and whose paid period plus the grace days is over goes
        // back to the Trial plan. Never touches a plan whose mandate is still running.
        recurringJobs.AddOrUpdate<AutoPayMaintenanceJob>(
            "autopay-lapsed-plans", job => job.EndLapsedPlansAsync(), "25 * * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Every 5 minutes: re-reads each tenant's QR connection from the bridge itself, so a missed connection event (a webhook that never arrived) cannot leave a
        // channel showing Connected - which would let sends go out - after the bridge has dropped it. Read-only on the bridge, idempotent, and a no-op when no tenant has a channel.
        recurringJobs.AddOrUpdate<WhatsAppBridgeHealthJob>(
            "whatsapp-bridge-health", job => job.RunAsync(), "*/5 * * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Twice a day, 10:00 and 17:00 IST (04:30 and 11:30 UTC): tells each QR tenant whose WhatsApp session has dropped, once per disconnect, to scan a new QR.
        // Sent from the platform's own WhatsApp number and email account; the tenant's wallet pays. A no-op when no session is down.
        recurringJobs.AddOrUpdate<WhatsAppSessionAlertJob>(
            "whatsapp-session-alert", job => job.RunAsync(), "30 4,11 * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Every 15 minutes: low-balance, used-up and credits-expiring alerts. Frequent so a tenant that has just
        // run out hears about it quickly; the notifier never raises the same alert twice, so it costs nothing.
        recurringJobs.AddOrUpdate<QuotaAlertJob>(
            "quota-alerts", job => job.RunAsync(), "*/15 * * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Daily at 03:20 UTC, clear of the reconciliation pass at 00:30: deletes what has outlived its retention period (webhook payloads,
        // dead tokens, read notifications, old AI turns and audit rows). Idempotent, so a repeated or overlapping run is harmless.
        recurringJobs.AddOrUpdate<DataRetentionJob>(
            "data-retention", job => job.RunAsync(), "20 3 * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Daily at 05:00 UTC: measures how full the database is and emails the platform operators once it reaches the alert level
        // (DatabaseSizeMonitor:ThresholdPercent, default 80%). Read-only, and the alert is deduped per day.
        recurringJobs.AddOrUpdate<DatabaseSizeMonitorJob>(
            "database-size-monitor", job => job.RunAsync(), "0 5 * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Daily at 04:10 UTC (after the data-retention pass at 03:20): refreshes every connected tenant's Facebook / Instagram
        // ad spend for the Revenue report. Idempotent - it re-reads the last 35 days and overwrites them - so a repeat is harmless.
        recurringJobs.AddOrUpdate<SocialAdSyncJob>(
            "social-ad-sync", job => job.RunAsync(), "10 4 * * *", RecurringJobPolicy.SkipMissedOccurrences);

        // Hourly at :40, clear of the subscription pass at :05 and the tenants' own template syncs at :00: sends the platform's notice
        // templates to Meta and brings their review status back. A no-op until the platform WhatsApp number is set up.
        recurringJobs.AddOrUpdate<PlatformTemplateSyncJob>(
            "platform-template-sync", job => job.RunAsync(), "40 * * * *", RecurringJobPolicy.SkipMissedOccurrences);
    }

    /// <summary>
    /// Deletes the global registrations the per-tenant jobs replaced. Necessary, not tidiness: Hangfire
    /// keeps a recurring job until something removes it, so on an existing deployment the old global jobs
    /// would otherwise keep firing alongside the new per-tenant ones - every tenant's campaigns processed
    /// twice a minute by two different jobs, with only the idempotency key standing between that and
    /// duplicate sends, and a leftover global token refresh still calling into code that no longer exists.
    ///
    /// Keyed off the catalog rather than a hard-coded list because the per-tenant ids deliberately reuse
    /// the old global ids as their prefix; <c>RemoveIfExists</c> is a no-op on a fresh database, so this
    /// costs one storage call per job type on every boot forever, which is the cheapest way to be certain
    /// no deployment is left with both.
    /// </summary>
    private static void RemoveLegacyGlobalTenantJobs(IRecurringJobManager recurringJobs)
    {
        foreach (var definition in TenantJobCatalog.All)
            recurringJobs.RemoveIfExists(definition.Key);
    }
}
