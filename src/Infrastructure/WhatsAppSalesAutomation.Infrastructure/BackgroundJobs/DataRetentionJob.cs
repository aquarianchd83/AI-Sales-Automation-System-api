using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Daily: clear out what has outlived its retention period. Idempotent, and never throws - a failed pass is logged and tried
/// again tomorrow, which costs nothing but a day of old rows.</summary>
public class DataRetentionJob
{
    private readonly IDataRetentionService _retention;
    private readonly ILogger<DataRetentionJob> _logger;

    public DataRetentionJob(IDataRetentionService retention, ILogger<DataRetentionJob> logger)
    {
        _retention = retention;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            await _retention.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data retention pass failed");
        }
    }
}
