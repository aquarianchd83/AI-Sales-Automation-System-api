using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hourly: pushes the platform's new and edited notice templates to Meta and pulls their review status back, so an approval shows up (and
/// a rejection is noticed) without someone pressing Sync. Idempotent, and never throws - a failed pass is logged and tried again next hour.
/// A no-op until the platform WhatsApp number is set up.</summary>
public class PlatformTemplateSyncJob
{
    private readonly IPlatformMessageTemplateService _templates;
    private readonly ILogger<PlatformTemplateSyncJob> _logger;

    public PlatformTemplateSyncJob(IPlatformMessageTemplateService templates, ILogger<PlatformTemplateSyncJob> logger)
    {
        _templates = templates;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            var result = await _templates.SyncAsync();
            if (!result.Configured)
                return;

            foreach (var failure in result.Failures)
                _logger.LogWarning("Platform template push failed: {Failure}", failure);

            _logger.LogInformation(
                "Platform templates synced: created={Created} updated={Updated} failed={Failed} remote={Remote} statusChanged={Changed}",
                result.Created, result.Updated, result.Failures.Count, result.RemoteCount, result.StatusUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Platform template sync failed");
        }
    }
}
