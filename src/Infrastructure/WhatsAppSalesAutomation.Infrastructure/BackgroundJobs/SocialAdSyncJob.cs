using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.SocialAds;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Daily: pulls the latest ad spend for every tenant that has connected Facebook / Instagram. Platform-global
/// (it walks every connected tenant), so a plain recurring job. Never throws - the next day starts clean, and a tenant
/// whose login has lapsed is marked "needs reconnect" for the Settings page to show, not retried forever.</summary>
public class SocialAdSyncJob
{
    private readonly ISocialAdSyncService _sync;
    private readonly ILogger<SocialAdSyncJob> _logger;

    public SocialAdSyncJob(ISocialAdSyncService sync, ILogger<SocialAdSyncJob> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            var synced = await _sync.SyncAllAsync();
            _logger.LogInformation("Social ad sync: {Count} tenant(s) synced", synced);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Social ad sync failed");
        }
    }
}
