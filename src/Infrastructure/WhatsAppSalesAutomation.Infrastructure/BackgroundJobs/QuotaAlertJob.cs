using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Quota;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Every fifteen minutes: raise the low-balance, used-up and credits-expiring alerts tenants have
/// earned since the last pass. Idempotent - the notifier never raises the same alert twice - and never throws.</summary>
public class QuotaAlertJob
{
    private readonly IQuotaAlertService _alerts;
    private readonly ILogger<QuotaAlertJob> _logger;

    public QuotaAlertJob(IQuotaAlertService alerts, ILogger<QuotaAlertJob> logger)
    {
        _alerts = alerts;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            var raised = await _alerts.EvaluateAsync();
            if (raised > 0)
                _logger.LogInformation("Quota alerts: {Raised} raised", raised);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quota alert pass failed");
        }
    }
}
