using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Credits;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hourly: give every tenant on a plan with monthly credits this month's credits (a month is granted once, so running often is harmless) and close
/// refund requests nobody answered. Plans never expire, so there is nothing to renew or warn about. Platform-global (it walks every tenant), so it is a
/// plain recurring job rather than one of the per-tenant schedules. Never throws - the next hour starts from a clean slate.</summary>
public class SubscriptionMaintenanceJob
{
    private readonly ICreditAllocationService _credits;
    private readonly IRefundService _refunds;
    private readonly ILogger<SubscriptionMaintenanceJob> _logger;

    public SubscriptionMaintenanceJob(ICreditAllocationService credits, IRefundService refunds, ILogger<SubscriptionMaintenanceJob> logger)
    {
        _credits = credits;
        _refunds = refunds;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            var granted = await _credits.AllocateMonthlyAsync();
            var closedRefunds = await _refunds.ExpireStaleAsync();
            _logger.LogInformation(
                "Subscription maintenance: {Granted} tenants given this month's plan credits, {Refunds} unanswered refund requests closed", granted, closedRefunds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription maintenance failed");
        }
    }
}
