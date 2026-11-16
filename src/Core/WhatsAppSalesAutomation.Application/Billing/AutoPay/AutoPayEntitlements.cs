using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>
/// What an AutoPay payment gives the tenant, done exactly once however many times it is asked:
/// <list type="number">
/// <item>The payment is recorded, and the tenant's plan and paid-through date are set - one save, so there is never a payment without the
/// plan it bought or a plan without its payment. The database allows one row per Razorpay payment id, so a second worker recording the
/// same payment at the same moment fails its save and is retried, by which time the row is there.</item>
/// <item>The plan's credits go into the existing wallet through the existing ledger, keyed by the Razorpay payment id: the ledger grants a
/// key once, so repeating this step adds nothing.</item>
/// <item>The tenant is told, once per payment.</item>
/// </list>
/// Each step is safe to repeat, so a failure anywhere is recovered by simply running the whole thing again.
/// </summary>
public class AutoPayEntitlements : IAutoPayEntitlements
{
    public const string Provider = "Razorpay";

    private readonly IApplicationDbContext _context;
    private readonly IPricingService _pricing;
    private readonly IQuotaLedgerService _ledger;
    private readonly ICreditAllocationService _credits;
    private readonly ITenantNotifier _notifier;
    private readonly ITenantJobProvisioner _jobs;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<AutoPayEntitlements> _logger;

    public AutoPayEntitlements(
        IApplicationDbContext context,
        IPricingService pricing,
        IQuotaLedgerService ledger,
        ICreditAllocationService credits,
        ITenantNotifier notifier,
        ITenantJobProvisioner jobs,
        IDateTimeProvider clock,
        ILogger<AutoPayEntitlements> logger)
    {
        _context = context;
        _pricing = pricing;
        _ledger = ledger;
        _credits = credits;
        _notifier = notifier;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The ledger key the credits for one Razorpay payment are granted under - what makes the grant happen once.</summary>
    public static string CreditKey(string razorpayPaymentId) => $"rzp-charge:{razorpayPaymentId}";

    // ── A payment was taken ─────────────────────────────────────────────────────────────

    public async Task ChargeSucceededAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default)
    {
        // What was taken must be what was agreed. Anything else is not credited: it stays as a failed event for a person to look at.
        if (payment.Amount != mandate.AmountMinor || !string.Equals(payment.Currency, mandate.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Payment {payment.Id} on {mandate.RazorpaySubscriptionId} is {RazorpayService.Format(payment.Amount, payment.Currency)}, " +
                $"but the mandate is for {RazorpayService.Format(mandate.AmountMinor, mandate.Currency)}. Nothing was credited.");

        var tenant = await _context.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == mandate.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {mandate.TenantId} of mandate {mandate.RazorpaySubscriptionId} does not exist.");
        var plan = await _context.Plans.FirstOrDefaultAsync(p => p.Id == mandate.PlanId, cancellationToken)
            ?? throw new InvalidOperationException($"Plan {mandate.PlanId} of mandate {mandate.RazorpaySubscriptionId} does not exist.");

        var now = _clock.UtcNow;
        var paidAt = payment.CreatedAtUtc ?? now;
        var periodStart = payment.PeriodStartUtc ?? mandate.CurrentStartUtc ?? paidAt;
        var periodEnd = payment.PeriodEndUtc ?? mandate.CurrentEndUtc ?? periodStart.AddMonths(1);

        // 1. The payment and the plan it pays for, in one save.
        var row = await FindPaymentAsync(payment.Id, cancellationToken);
        var firstTime = row is null || row.Status != PaymentStatus.Succeeded;
        if (row is null)
        {
            row = NewPayment(mandate, tenant, plan.Name, payment, paidAt, periodStart, periodEnd);
            _context.Payments.Add(row);
        }
        else if (row.Status != PaymentStatus.Succeeded)
        {
            // Razorpay first reported this very payment as failed and it has since gone through (a late authorisation).
            row.Status = PaymentStatus.Succeeded;
            row.FailureReason = null;
            row.PaidAtUtc = paidAt;
            row.PeriodStartUtc = periodStart;
            row.PeriodEndUtc = periodEnd;
        }

        var subscription = await _context.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == mandate.TenantId, cancellationToken);
        if (subscription is null)
        {
            subscription = new Subscription { TenantId = mandate.TenantId };
            _context.Subscriptions.Add(subscription);
        }

        subscription.PlanId = plan.Id;
        subscription.Status = SubscriptionStatus.Active;
        subscription.RazorpaySubscriptionRowId = mandate.Id;
        subscription.CancelAtPeriodEnd = mandate.CancelAtCycleEnd || RazorpayMandateState.IsEnded(mandate.Status);
        // Never backwards: a payment for an earlier cycle, arriving late, must not shorten what a later one already paid for.
        if (subscription.CurrentPeriodEndUtc is null || periodEnd > subscription.CurrentPeriodEndUtc)
        {
            subscription.CurrentPeriodStartUtc = periodStart;
            subscription.CurrentPeriodEndUtc = periodEnd;
        }

        // Paying is what brings a tenant back - from the trial, or from having lapsed. An account an operator removed stays removed.
        if (tenant.Status != TenantStatus.Deleted)
        {
            tenant.Status = TenantStatus.Active;
            tenant.TrialEndsAtUtc = null;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // 2. The credits, through the existing ledger. Each key is granted once, so this adds nothing when repeated.
        await _ledger.GrantPlanCreditsAsync(
            mandate.TenantId, plan.StarterCredits, $"starter:{plan.Id:N}", $"{plan.Name} plan starter credits", QuotaGrantOrigin.Trial, null, cancellationToken);
        var granted = await _ledger.GrantPlanCreditsAsync(
            mandate.TenantId, plan.MonthlyCredits, CreditKey(payment.Id), $"{plan.Name} plan credits for the period to {periodEnd:d MMM yyyy}",
            QuotaGrantOrigin.PlanAllocation, row.Id, cancellationToken);

        // 3. The tenant's jobs follow its status, as they do when a plan is chosen any other way.
        await _jobs.SyncTenantAsync(mandate.TenantId, cancellationToken);

        // 4. One notice per payment, whatever happened above on an earlier try.
        var amount = $"{row.CurrencySymbol}{row.TotalPaidLocal:#,##0.00}";
        var credits = plan.MonthlyCredits > 0 ? $" {plan.MonthlyCredits:#,##0.##} credits were added to your wallet." : string.Empty;
        await _notifier.NotifyAsync(
            new TenantNotificationRequest(
                mandate.TenantId, TenantNotificationKind.AutoPayPaymentReceived, null, payment.Id,
                "Payment received",
                $"{amount} for the {plan.Name} plan was paid automatically.{credits} Your plan is paid until {periodEnd:d MMM yyyy}.",
                AlsoWhatsApp: false),
            cancellationToken);

        _logger.LogInformation(
            "AutoPay payment {PaymentId} on {SubscriptionId} for tenant {TenantId}: recorded {Recorded}, credits granted {Granted}, paid to {PeriodEnd:u}",
            payment.Id, mandate.RazorpaySubscriptionId, mandate.TenantId, firstTime, granted, periodEnd);
    }

    // ── A payment failed ────────────────────────────────────────────────────────────────

    public async Task ChargeFailedAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default)
    {
        // Already on record - as this failure, or as a success that a late "failed" must not undo.
        if (await FindPaymentAsync(payment.Id, cancellationToken) is not null)
            return;

        var tenant = await _context.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == mandate.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {mandate.TenantId} of mandate {mandate.RazorpaySubscriptionId} does not exist.");
        var planName = await _context.Plans.Where(p => p.Id == mandate.PlanId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var attemptedAt = payment.CreatedAtUtc ?? _clock.UtcNow;
        var row = NewPayment(mandate, tenant, planName, payment, attemptedAt, payment.PeriodStartUtc, payment.PeriodEndUtc);
        row.Status = PaymentStatus.Failed;
        row.FailureReason = Truncate(string.IsNullOrWhiteSpace(payment.ErrorDescription) ? "The payment did not go through." : payment.ErrorDescription.Trim(), 500);
        _context.Payments.Add(row);
        await _context.SaveChangesAsync(cancellationToken);

        // No credits, no change of plan, and no notice from here: the tenant hears once per cycle, when the mandate itself reports the
        // failure, rather than once for every retry Razorpay makes.
        _logger.LogWarning(
            "AutoPay payment {PaymentId} on {SubscriptionId} for tenant {TenantId} failed: {Reason}",
            payment.Id, mandate.RazorpaySubscriptionId, mandate.TenantId, row.FailureReason);
    }

    // ── The mandate changed ─────────────────────────────────────────────────────────────

    public async Task MandateChangedAsync(RazorpaySubscription mandate, RazorpaySubscriptionStatus previous, CancellationToken cancellationToken = default)
    {
        // Only the mandate that is behind the tenant's plan says anything about that plan. One that never took a payment has nothing to say.
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == mandate.TenantId && s.RazorpaySubscriptionRowId == mandate.Id, cancellationToken);
        if (subscription is null)
            return;

        var planName = await _context.Plans.Where(p => p.Id == mandate.PlanId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken) ?? "your";
        var paidUntil = subscription.CurrentPeriodEndUtc is { } end ? $" until {end:d MMM yyyy}" : string.Empty;
        // One notice per state per billing cycle: a mandate that halts again next quarter is news again.
        var episode = $"{mandate.RazorpaySubscriptionId}:{subscription.CurrentPeriodEndUtc:yyyyMMdd}";

        switch (mandate.Status)
        {
            case RazorpaySubscriptionStatus.Active:
            case RazorpaySubscriptionStatus.Authenticated:
                // Running again after a failed payment or a pause.
                if (subscription.Status == SubscriptionStatus.PastDue)
                    subscription.Status = SubscriptionStatus.Active;
                await _context.SaveChangesAsync(cancellationToken);
                break;

            case RazorpaySubscriptionStatus.Pending:
                // Razorpay is still trying, and the plan keeps working through the grace days: only the tenant needs to know.
                await NotifyAsync(mandate, TenantNotificationKind.AutoPayPaymentFailed, episode,
                    "Your automatic payment didn't go through",
                    $"The payment for the {planName} plan could not be taken. It will be tried again automatically over the next few days - please make sure your UPI account has enough balance.",
                    cancellationToken);
                break;

            case RazorpaySubscriptionStatus.Halted:
                // Every retry failed. Nothing more will be taken, so the plan is on borrowed time.
                subscription.Status = SubscriptionStatus.PastDue;
                await _context.SaveChangesAsync(cancellationToken);
                await NotifyAsync(mandate, TenantNotificationKind.AutoPayHalted, episode,
                    "Automatic payment has stopped",
                    $"The payment for the {planName} plan failed several times and will not be tried again. Set up automatic payment again from Billing to keep your plan.",
                    cancellationToken);
                break;

            case RazorpaySubscriptionStatus.Paused:
                await NotifyAsync(mandate, TenantNotificationKind.AutoPayPaused, episode,
                    "Automatic payment is paused",
                    $"Automatic payment for the {planName} plan was paused, so nothing will be taken. Your plan stays active{paidUntil}; resume the mandate in your UPI app to keep it after that.",
                    cancellationToken);
                break;

            case RazorpaySubscriptionStatus.Cancelled:
            case RazorpaySubscriptionStatus.Completed:
            case RazorpaySubscriptionStatus.Expired:
                // Paid-for access is kept: this only records that it will not renew.
                subscription.CancelAtPeriodEnd = true;
                await _context.SaveChangesAsync(cancellationToken);
                await NotifyAsync(mandate, TenantNotificationKind.AutoPayCancelled, episode,
                    "Automatic payment has ended",
                    $"Automatic payment for the {planName} plan has ended and nothing more will be taken. Your plan stays active{paidUntil}.",
                    cancellationToken);
                break;
        }
    }

    // ── The paid plan is over ───────────────────────────────────────────────────────────

    public async Task<bool> EndPaidAccessAsync(RazorpaySubscription mandate, CancellationToken cancellationToken = default)
    {
        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == mandate.TenantId && s.RazorpaySubscriptionRowId == mandate.Id, cancellationToken);
        if (subscription is null)
            return false;

        // Checked here as well as by the caller: a payment may have landed between its query and now.
        var graceEnds = subscription.CurrentPeriodEndUtc?.AddDays(AutoPayOptions.GraceDays);
        if (graceEnds is null || graceEnds > _clock.UtcNow)
            return false;

        var planName = await _context.Plans.Where(p => p.Id == mandate.PlanId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken) ?? "paid";
        var paidPlanId = subscription.PlanId;

        // Back to the Trial plan, the way a tenant gets onto it anywhere else. Credits already in the wallet are untouched.
        await _credits.AssignTrialPlanAsync(mandate.TenantId, cancellationToken);

        subscription = await _context.Subscriptions.IgnoreQueryFilters().FirstAsync(s => s.TenantId == mandate.TenantId, cancellationToken);
        if (subscription.PlanId == paidPlanId)
        {
            // No Trial plan to fall back to: the paid one simply ends.
            subscription.PlanId = null;
            subscription.Status = SubscriptionStatus.Canceled;
            subscription.CurrentPeriodStartUtc = null;
            subscription.CurrentPeriodEndUtc = null;
        }
        subscription.RazorpaySubscriptionRowId = null;
        subscription.CancelAtPeriodEnd = false;
        await _context.SaveChangesAsync(cancellationToken);

        await _jobs.SyncTenantAsync(mandate.TenantId, cancellationToken);
        await NotifyAsync(mandate, TenantNotificationKind.PlanRenewalFailed, $"{mandate.RazorpaySubscriptionId}:ended",
            $"Your {planName} plan has ended",
            $"The {planName} plan was not renewed, so your account is back on the free plan. The credits in your wallet are still yours. Subscribe again from Billing whenever you like.",
            cancellationToken);

        _logger.LogInformation("AutoPay: tenant {TenantId} left the {PlanName} plan; mandate {SubscriptionId} is {Status}", mandate.TenantId, planName, mandate.RazorpaySubscriptionId, mandate.Status);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private Task<Payment?> FindPaymentAsync(string razorpayPaymentId, CancellationToken cancellationToken) =>
        _context.Payments.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Provider == Provider && p.ProviderPaymentId == razorpayPaymentId, cancellationToken);

    /// <summary>The payment row for what the mandate debits: the same snapshot every other payment records (price before tax, each tax line,
    /// total, rupee equivalent), worked back from the amount actually agreed so that the total on record is exactly what was taken.</summary>
    private Payment NewPayment(
        RazorpaySubscription mandate, Tenant tenant, string planName, RazorpayWebhookPayment payment, DateTime at, DateTime? periodStart, DateTime? periodEnd)
    {
        var row = PaymentFactory.For(
            mandate.TenantId, PaymentKind.Subscription, planName, QuoteFor(mandate, tenant), at, planId: mandate.PlanId,
            periodStartUtc: periodStart, periodEndUtc: periodEnd);
        row.Provider = Provider;
        row.ProviderPaymentId = payment.Id;
        row.ProviderInvoiceId = payment.InvoiceId;
        row.ProviderSubscriptionId = mandate.RazorpaySubscriptionId;
        row.PaymentMethod = payment.Method ?? mandate.PaymentMethod;
        row.Status = PaymentStatus.Succeeded;
        return row;
    }

    private PriceQuote QuoteFor(RazorpaySubscription mandate, Tenant tenant)
    {
        var total = mandate.AmountMinor / 100m;
        var region = RegionalPricingCatalog.Resolve(tenant.CountryCode);

        // total = subtotal + tax, and tax is a percentage of the subtotal.
        var ratePercent = _pricing.TaxOn(100m, region.CountryCode, tenant.StateCode).Sum(l => l.RatePercent);
        var subtotal = Math.Round(total / (1 + ratePercent / 100m), 2, MidpointRounding.AwayFromZero);
        var quote = _pricing.Quote(subtotal, tenant.CountryCode, tenant.StateCode, allowFree: true)
            ?? throw new InvalidOperationException($"The payment on {mandate.RazorpaySubscriptionId} could not be priced for tenant {tenant.Id}.");
        if (quote.Total == total)
            return quote;

        // A paisa of rounding either way: the last tax line absorbs it, so the lines still add up to what was taken.
        var lines = quote.TaxLines.ToList();
        var difference = total - quote.Total;
        if (lines.Count > 0)
            lines[^1] = lines[^1] with { Amount = lines[^1].Amount + difference };
        else
            subtotal += difference;

        return quote with
        {
            Subtotal = subtotal,
            TaxLines = lines,
            Tax = lines.Sum(l => l.Amount),
            Total = total,
            TotalInr = Math.Round(total * quote.FxRateToInr, 2, MidpointRounding.AwayFromZero)
        };
    }

    private Task<bool> NotifyAsync(
        RazorpaySubscription mandate, TenantNotificationKind kind, string episode, string title, string body, CancellationToken cancellationToken) =>
        _notifier.NotifyAsync(new TenantNotificationRequest(mandate.TenantId, kind, null, episode, title, body, AlsoWhatsApp: false), cancellationToken);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
