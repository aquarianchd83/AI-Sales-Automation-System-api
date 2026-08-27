using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Daily check that keeps the WhatsApp Cloud API access token from expiring - see
/// WhatsAppTokenRefreshService for the actual Meta OAuth exchange. A no-op most days (only actually
/// calls Meta once the token is within its refresh window), so a daily cadence against a ~60-day
/// token lifetime is deliberately generous, not tightly timed.</summary>
public class WhatsAppTokenRefreshJob
{
    private readonly IWhatsAppTokenRefreshService _refreshService;
    private readonly ILogger<WhatsAppTokenRefreshJob> _logger;

    public WhatsAppTokenRefreshJob(IWhatsAppTokenRefreshService refreshService, ILogger<WhatsAppTokenRefreshJob> logger)
    {
        _refreshService = refreshService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RunAsync()
    {
        try
        {
            await _refreshService.RefreshIfNeededAsync();
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders: WhatsAppTokenRefreshService already catches its own HTTP/JSON
            // failures internally and just logs, but this job must never throw regardless - an
            // unhandled exception here would surface as a permanently-failing Hangfire job, which is
            // worse than a quietly-skipped refresh attempt that tries again tomorrow.
            _logger.LogError(ex, "WhatsAppTokenRefreshJob failed unexpectedly");
        }
    }
}
