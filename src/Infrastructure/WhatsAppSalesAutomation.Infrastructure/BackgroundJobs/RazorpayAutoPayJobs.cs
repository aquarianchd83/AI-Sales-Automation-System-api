using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Billing.AutoPay;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Application.Wallet;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hands a stored Razorpay webhook event to <see cref="RazorpayWebhookProcessingJob"/>.</summary>
public class HangfireRazorpayWebhookQueue : IRazorpayWebhookQueue
{
    private readonly IBackgroundJobClient _client;

    public HangfireRazorpayWebhookQueue(IBackgroundJobClient client) => _client = client;

    public void Enqueue(Guid webhookEventRowId) =>
        _client.Enqueue<RazorpayWebhookProcessingJob>(job => job.RunAsync(webhookEventRowId));
}

/// <summary>
/// Enqueued once per stored Razorpay subscription webhook (see RazorpayService.HandleWebhookAsync). Like InboundWebhookProcessingJob it
/// has no [DisableConcurrentExecution]: different events are independent. It does not reschedule itself on failure - the processor leaves
/// the row Failed, and <see cref="AutoPayMaintenanceJob.RetryWebhooksAsync"/> tries it again with a growing wait, so a retry survives a
/// restart and is visible in the table rather than only in Hangfire. Never throws, so Hangfire's own automatic retries (which know nothing
/// of that wait) stay out of it.
/// </summary>
public class RazorpayWebhookProcessingJob
{
    private readonly IRazorpaySubscriptionEventProcessor _processor;
    private readonly ILogger<RazorpayWebhookProcessingJob> _logger;

    public RazorpayWebhookProcessingJob(IRazorpaySubscriptionEventProcessor processor, ILogger<RazorpayWebhookProcessingJob> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    public async Task RunAsync(Guid webhookEventRowId)
    {
        try
        {
            var outcome = await _processor.ProcessAsync(webhookEventRowId);
            _logger.LogInformation("Razorpay webhook event row {Id}: {Outcome}", webhookEventRowId, outcome);
        }
        catch (Exception ex)
        {
            // The processor records its own failures; this is only for something going wrong around it (the database being away). The row
            // is still Received or Failed, so the retry pass picks it up.
            _logger.LogError(ex, "Razorpay webhook event row {Id} could not be processed; the retry pass will try again", webhookEventRowId);
        }
    }
}

/// <summary>
/// AutoPay's two recurring passes. Platform-global (they walk every tenant's mandates), so plain recurring jobs rather than per-tenant
/// schedules. Neither ever starts a debit: Razorpay alone charges a mandate. Both are safe to run as often as you like, and neither throws -
/// the next run starts from a clean slate.
/// </summary>
public class AutoPayMaintenanceJob
{
    private readonly IAutoPayMaintenanceService _maintenance;
    private readonly ILogger<AutoPayMaintenanceJob> _logger;

    public AutoPayMaintenanceJob(IAutoPayMaintenanceService maintenance, ILogger<AutoPayMaintenanceJob> logger)
    {
        _maintenance = maintenance;
        _logger = logger;
    }

    /// <summary>Applies stored webhook events that are still waiting or are due another try.</summary>
    public async Task RetryWebhooksAsync()
    {
        try
        {
            var tried = await _maintenance.RetryWebhooksAsync();
            if (tried > 0)
                _logger.LogInformation("Razorpay webhook retry pass: {Tried} events tried", tried);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Razorpay webhook retry pass failed");
        }
    }

    /// <summary>Moves plans whose mandate stopped paying, and whose paid period and grace are over, back to the Trial plan.</summary>
    public async Task EndLapsedPlansAsync()
    {
        try
        {
            var ended = await _maintenance.EndLapsedPlansAsync();
            if (ended > 0)
                _logger.LogInformation("AutoPay: {Ended} lapsed plans ended", ended);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoPay lapsed-plan pass failed");
        }
    }

    /// <summary>Compares every live mandate with Razorpay's own record and repairs what a missed webhook left behind.</summary>
    public async Task ReconcileAsync()
    {
        try
        {
            await _maintenance.ReconcileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoPay reconciliation failed");
        }
    }
}

/// <summary>
/// Settles wallet top-ups that never heard back - the browser was closed mid-payment, or the webhook was lost - by asking Razorpay what became of each
/// order, and calls abandoned checkouts abandoned. Safe to run as often as you like: crediting is exactly-once, and it never throws, so the next run
/// simply starts from a clean slate.
/// </summary>
public class WalletRechargeReconciliationJob
{
    private readonly IWalletRechargeService _recharge;
    private readonly ILogger<WalletRechargeReconciliationJob> _logger;

    public WalletRechargeReconciliationJob(IWalletRechargeService recharge, ILogger<WalletRechargeReconciliationJob> logger)
    {
        _recharge = recharge;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            await _recharge.ReconcilePendingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wallet recharge reconciliation pass failed");
        }
    }
}
