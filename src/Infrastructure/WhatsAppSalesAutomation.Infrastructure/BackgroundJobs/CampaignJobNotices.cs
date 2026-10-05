using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Runs one campaign job a campaign at a time and tells the tenant about each campaign by name - "Campaign "Diwali
/// Offer" initial sends started" - because a notice that only says the job ran does not say which campaign it was about.
/// The send service already takes a campaign id, so this is the same work as one run across every campaign, split up.</summary>
public static class CampaignJobNotices
{
    /// <summary>The job types that report per campaign, so <see cref="TenantJobRunner"/> leaves their tenant-wide notices to this.</summary>
    public static readonly IReadOnlySet<string> PerCampaignJobTypes = new HashSet<string>
    {
        TenantJobTypes.CampaignInitialSends,
        TenantJobTypes.CampaignFollowUps,
        TenantJobTypes.CampaignSendRetries,
        TenantJobTypes.CampaignCompletion,
    };

    /// <param name="label">What the job does, as it reads after the campaign name: "initial sends", "follow-ups".</param>
    /// <param name="includeDueScheduled">Initial sends also start a Scheduled campaign whose start time has come.</param>
    /// <param name="runForCampaign">Does the job for one campaign and returns the detail for its "completed" notice.</param>
    /// <returns>One line for the schedule row: how many campaigns it covered, and their combined detail.</returns>
    public static async Task<string?> RunAsync(
        IServiceProvider services,
        Guid tenantId,
        string label,
        bool includeDueScheduled,
        Func<ICampaignSendService, Guid, Task<string>> runForCampaign,
        CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<IApplicationDbContext>();
        var notifier = services.GetRequiredService<ITenantNotifier>();
        var sendService = services.GetRequiredService<ICampaignSendService>();

        var localNow = includeDueScheduled
            ? await services.GetRequiredService<ITenantTimeZoneProvider>().GetLocalNowAsync(cancellationToken)
            : default;

        var campaigns = await context.Campaigns
            .Where(c => c.Status == CampaignStatus.Running
                || (includeDueScheduled && c.Status == CampaignStatus.Scheduled && c.ScheduledStartAt != null && c.ScheduledStartAt <= localNow))
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(cancellationToken);

        var details = new List<string>();
        foreach (var campaign in campaigns)
        {
            await NotifyAsync(notifier, tenantId, TenantNotificationKind.JobStarted,
                $"Campaign \"{campaign.Name}\" {label} started",
                $"Campaign \"{campaign.Name}\": {label} has started running for your account.", cancellationToken);

            var detail = await runForCampaign(sendService, campaign.Id);
            details.Add($"\"{campaign.Name}\": {detail}");

            await NotifyAsync(notifier, tenantId, TenantNotificationKind.JobCompleted,
                $"Campaign \"{campaign.Name}\" {label} completed",
                $"Campaign \"{campaign.Name}\": {label} finished running. {detail}", cancellationToken);
        }

        return campaigns.Count == 0 ? "No campaign to run." : $"{campaigns.Count} campaign(s). {string.Join("; ", details)}";
    }

    public static string Describe(SendRunResult result) =>
        $"considered={result.Considered} sent={result.Sent} failed={result.Failed} skipped={result.Skipped}";

    // Every call is its own episode, so a job that runs daily gets a fresh pair each day rather than being deduped away.
    private static Task NotifyAsync(
        ITenantNotifier notifier, Guid tenantId, TenantNotificationKind kind, string title, string body, CancellationToken cancellationToken) =>
        notifier.NotifyAsync(
            new TenantNotificationRequest(tenantId, kind, null, Guid.NewGuid().ToString("N"), title, body, AlsoWhatsApp: false),
            cancellationToken);
}
