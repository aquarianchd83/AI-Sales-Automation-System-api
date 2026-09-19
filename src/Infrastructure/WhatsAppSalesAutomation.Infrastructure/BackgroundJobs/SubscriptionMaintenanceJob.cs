using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Billing.Refunds;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hourly: renew subscriptions whose period has ended, then expire lapsed quota. Platform-global
/// (it walks every tenant), so it is a plain recurring job rather than one of the per-tenant schedules.
/// Never throws - the next hour starts from a clean slate, and a permanently failing job is just noise.</summary>
public class SubscriptionMaintenanceJob
{
    private readonly ISubscriptionRenewalService _renewal;
    private readonly IRefundService _refunds;
    private readonly ILogger<SubscriptionMaintenanceJob> _logger;

    public SubscriptionMaintenanceJob(ISubscriptionRenewalService renewal, IRefundService refunds, ILogger<SubscriptionMaintenanceJob> logger)
    {
        _renewal = renewal;
        _refunds = refunds;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            var (renewed, expired) = await _renewal.RunAsync();
            var closedRefunds = await _refunds.ExpireStaleAsync();
            _logger.LogInformation(
                "Subscription maintenance: {Renewed} renewed, {Expired} quota grants expired, {Refunds} unanswered refund requests closed",
                renewed, expired, closedRefunds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription maintenance failed");
        }
    }
}
