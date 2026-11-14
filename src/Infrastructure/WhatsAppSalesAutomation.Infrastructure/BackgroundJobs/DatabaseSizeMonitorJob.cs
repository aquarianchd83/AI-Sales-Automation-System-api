using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Infrastructure.Maintenance;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Daily: check how full the database is and email the platform operators when it reaches the alert level (default 80%).
/// Read-only and idempotent; never throws - a failed pass is logged and tried again tomorrow.</summary>
public class DatabaseSizeMonitorJob
{
    private readonly DatabaseSizeMonitorService _monitor;
    private readonly ILogger<DatabaseSizeMonitorJob> _logger;

    public DatabaseSizeMonitorJob(DatabaseSizeMonitorService monitor, ILogger<DatabaseSizeMonitorJob> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            await _monitor.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database size check failed");
        }
    }
}
