using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>What one reconciliation pass found and did.</summary>
/// <param name="Checked">Mandates compared with Razorpay.</param>
/// <param name="Repaired">Mandates whose status here was behind Razorpay's - a webhook was missed or is still on its way.</param>
/// <param name="AbandonedCancelled">Authorisations nobody completed, cancelled at Razorpay.</param>
/// <param name="PaymentsMissing">Mandates where Razorpay still counts more payments than are recorded here, after trying to recover them.</param>
/// <param name="Errors">Mandates that could not be checked this time.</param>
/// <param name="PaymentsRecovered">Payments Razorpay took that were not on record here - a charge event never arrived - and now are, with their credits.</param>
public record AutoPayReconciliationResult(int Checked, int Repaired, int AbandonedCancelled, int PaymentsMissing, int Errors, int PaymentsRecovered = 0);

/// <summary>The housekeeping AutoPay needs because webhooks are not a guaranteed delivery: retrying events that could not be applied, and
/// comparing every live mandate with Razorpay's own record. Neither ever starts a debit - Razorpay alone does that.</summary>
public interface IAutoPayMaintenanceService
{
    /// <summary>Applies stored subscription events that are still waiting: ones no job picked up, and ones that failed and are due another try.</summary>
    /// <returns>How many were tried.</returns>
    Task<int> RetryWebhooksAsync(CancellationToken cancellationToken = default);

    Task<AutoPayReconciliationResult> ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends the paid plan of every tenant whose mandate has stopped paying and whose paid period, plus the grace days, is over.</summary>
    /// <returns>How many plans were ended.</returns>
    Task<int> EndLapsedPlansAsync(CancellationToken cancellationToken = default);
}

public class AutoPayMaintenanceService : IAutoPayMaintenanceService
{
    private const int RetryBatch = 100;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    private readonly IApplicationDbContext _context;
    private readonly IAppSettingsStore _store;
    private readonly IRazorpayGateway _gateway;
    private readonly IRazorpaySubscriptionEventProcessor _processor;
    private readonly IAutoPayEntitlements _entitlements;
    private readonly IDateTimeProvider _clock;
    private readonly AutoPayOptions _options;
    private readonly ILogger<AutoPayMaintenanceService> _logger;

    public AutoPayMaintenanceService(
        IApplicationDbContext context,
        IAppSettingsStore store,
        IRazorpayGateway gateway,
        IRazorpaySubscriptionEventProcessor processor,
        IAutoPayEntitlements entitlements,
        IDateTimeProvider clock,
        IOptionsSnapshot<AutoPayOptions> options,
        ILogger<AutoPayMaintenanceService> logger)
    {
        _context = context;
        _store = store;
        _gateway = gateway;
        _processor = processor;
        _entitlements = entitlements;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    // ── Webhook retries ─────────────────────────────────────────────────────────────────

    /// <summary>How long to wait after the nth failure before trying again: 5 minutes, then doubling, capped at 6 hours.</summary>
    public static TimeSpan BackoffAfter(int failures)
    {
        var minutes = 5d * Math.Pow(2, Math.Clamp(failures - 1, 0, 20));
        return minutes >= MaxBackoff.TotalMinutes ? MaxBackoff : TimeSpan.FromMinutes(minutes);
    }

    public async Task<int> RetryWebhooksAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var maxAttempts = Math.Max(1, _options.WebhookMaxAttempts);
        var pickupBefore = now.AddMinutes(-Math.Max(0, _options.WebhookPickupMinutes));

        // Received and still waiting: the job that should have applied it never ran.
        var waiting = await _context.RazorpayWebhookEvents.AsNoTracking()
            .Where(e => e.ProcessingStatus == RazorpayWebhookProcessingStatus.Received && e.CreatedAt <= pickupBefore)
            .OrderBy(e => e.CreatedAt)
            .Take(RetryBatch)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var failed = await _context.RazorpayWebhookEvents.AsNoTracking()
            .Where(e => e.ProcessingStatus == RazorpayWebhookProcessingStatus.Failed && e.RetryCount < maxAttempts)
            .OrderBy(e => e.CreatedAt)
            .Take(RetryBatch)
            .ToListAsync(cancellationToken);
        var due = failed
            .Where(e => e.ProcessedAtUtc is null || e.ProcessedAtUtc.Value + BackoffAfter(e.RetryCount) <= now)
            .Select(e => e.Id);

        var tried = 0;
        foreach (var id in waiting.Concat(due))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Each event on a clean slate: nothing one left tracked may ride along on the next one's save.
            _context.ResetChangeTracker();
            var outcome = await _processor.ProcessAsync(id, cancellationToken);
            tried++;

            if (outcome == RazorpayEventOutcome.Failed)
                await WarnIfGivenUpAsync(id, maxAttempts, cancellationToken);
        }

        return tried;
    }

    private async Task WarnIfGivenUpAsync(Guid id, int maxAttempts, CancellationToken cancellationToken)
    {
        var record = await _context.RazorpayWebhookEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (record is not null && record.RetryCount >= maxAttempts)
            _logger.LogCritical(
                "Razorpay webhook {Event} ({EventId}) for {SubscriptionId} failed {Attempts} times and will not be retried again: {Error}. It stays Failed and needs a person.",
                record.Event, record.EventId, record.SubscriptionId, record.RetryCount, record.ErrorMessage);
    }

    // ── Reconciliation ──────────────────────────────────────────────────────────────────

    public async Task<AutoPayReconciliationResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        // Test and Live keys are both kept: each mandate is checked with the keys of the mode it was made in (a test mandate does not exist
        // for the live account), so switching the active mode does not leave the other mode's mandates unwatched.
        var all = (await AutoPaySettings.ReadAsync(_store, cancellationToken)).AllCredentials;
        if (all.Count == 0)
            return new AutoPayReconciliationResult(0, 0, 0, 0, 0);

        var ended = RazorpayMandateState.Ended;
        var work = new List<(Guid Id, RazorpayCredentials Credentials)>();
        foreach (var keys in all)
        {
            var mode = keys.Mode;
            var modeIds = await _context.RazorpaySubscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => !ended.Contains(s.Status) && s.Mode == mode)
                .OrderBy(s => s.LastReconciledAtUtc)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
            work.AddRange(modeIds.Select(i => (i, keys)));
        }

        int checkedCount = 0, repaired = 0, abandoned = 0, missing = 0, errors = 0, recovered = 0;
        foreach (var (id, credentials) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _context.ResetChangeTracker();
            try
            {
                var outcome = await ReconcileOneAsync(id, credentials, cancellationToken);
                checkedCount++;
                if (outcome.Repaired) repaired++;
                if (outcome.AbandonedCancelled) abandoned++;
                if (outcome.PaymentsMissing) missing++;
                recovered += outcome.PaymentsRecovered;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One mandate that cannot be checked must not stop the rest being checked.
                errors++;
                _logger.LogError(ex, "AutoPay reconciliation failed for mandate {MandateId}", id);
            }
        }

        _logger.LogInformation(
            "AutoPay reconciliation: {Checked} mandates checked, {Repaired} brought up to date, {Abandoned} abandoned authorisations cancelled, {Recovered} missed payments recovered, {Missing} still with payments missing, {Errors} errors",
            checkedCount, repaired, abandoned, recovered, missing, errors);
        return new AutoPayReconciliationResult(checkedCount, repaired, abandoned, missing, errors, recovered);
    }

    private async Task<(bool Repaired, bool AbandonedCancelled, bool PaymentsMissing, int PaymentsRecovered)> ReconcileOneAsync(
        Guid id, RazorpayCredentials credentials, CancellationToken cancellationToken)
    {
        var mandate = await _context.RazorpaySubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (mandate is null || RazorpayMandateState.IsEnded(mandate.Status))
            return (false, false, false, 0);

        var fetched = await _gateway.FetchSubscriptionAsync(credentials, mandate.RazorpaySubscriptionId, cancellationToken);
        if (!fetched.Success || fetched.Value is null)
            throw new InvalidOperationException($"Razorpay could not be asked about {mandate.RazorpaySubscriptionId}: {fetched.Error}");

        var now = _clock.UtcNow;
        var previous = mandate.Status;
        Apply(mandate, fetched.Value, now);

        // Started and never authorised: closed at Razorpay too, so a link left in an old tab cannot be approved weeks later.
        var abandoned = false;
        if (mandate.Status == RazorpaySubscriptionStatus.Created && _options.AbandonedAuthorizationHours > 0
            && mandate.CreatedAt <= now.AddHours(-_options.AbandonedAuthorizationHours))
        {
            var cancelled = await _gateway.CancelSubscriptionAsync(credentials, mandate.RazorpaySubscriptionId, false, cancellationToken);
            if (!cancelled.Success || cancelled.Value is null)
                throw new InvalidOperationException($"The abandoned authorisation {mandate.RazorpaySubscriptionId} could not be cancelled at Razorpay: {cancelled.Error}");
            Apply(mandate, cancelled.Value, now);
            RazorpayMandateState.MarkEnded(mandate, RazorpaySubscriptionStatus.Cancelled, now);
            abandoned = true;
        }

        mandate.LastReconciledAtUtc = now;
        await _context.SaveChangesAsync(cancellationToken);

        var repaired = mandate.Status != previous && !abandoned;
        if (repaired)
            _logger.LogWarning(
                "AutoPay reconciliation: mandate {SubscriptionId} was {Previous} here but {Current} at Razorpay; brought up to date",
                mandate.RazorpaySubscriptionId, previous, mandate.Status);
        if (mandate.Status != previous)
            await _entitlements.MandateChangedAsync(mandate, previous, cancellationToken);

        // Razorpay has counted payments that are not on record here: a charge event never arrived or never applied.
        var recovered = 0;
        var paymentsMissing = fetched.Value.PaidCount > await RecordedPaymentsAsync(mandate.RazorpaySubscriptionId, cancellationToken);
        if (paymentsMissing)
        {
            recovered = await RecoverPaymentsAsync(mandate, credentials, cancellationToken);
            var recorded = await RecordedPaymentsAsync(mandate.RazorpaySubscriptionId, cancellationToken);
            paymentsMissing = fetched.Value.PaidCount > recorded;
            if (paymentsMissing)
                _logger.LogError(
                    "AutoPay reconciliation: Razorpay counts {Paid} payments on {SubscriptionId} but {Recorded} are recorded here, and its invoices did not account for the rest",
                    fetched.Value.PaidCount, mandate.RazorpaySubscriptionId, recorded);
        }

        return (repaired, abandoned, paymentsMissing, recovered);
    }

    private Task<int> RecordedPaymentsAsync(string razorpaySubscriptionId, CancellationToken cancellationToken) =>
        _context.Payments.IgnoreQueryFilters()
            .CountAsync(p => p.ProviderSubscriptionId == razorpaySubscriptionId && p.Status == PaymentStatus.Succeeded && p.Kind == PaymentKind.Subscription, cancellationToken);

    /// <summary>
    /// Records the payments Razorpay took that never reached us. Razorpay raises an invoice for each billing cycle and a paid one names its
    /// payment, so each paid invoice whose payment is not on record is handed to the same code a charge webhook reaches - which records a
    /// payment and grants its credits exactly once, so a payment whose webhook is merely late, and arrives afterwards, is not counted twice.
    /// </summary>
    private async Task<int> RecoverPaymentsAsync(RazorpaySubscription mandate, RazorpayCredentials credentials, CancellationToken cancellationToken)
    {
        var invoices = await _gateway.FetchSubscriptionInvoicesAsync(credentials, mandate.RazorpaySubscriptionId, cancellationToken);
        if (!invoices.Success || invoices.Value is null)
            throw new InvalidOperationException($"The invoices of {mandate.RazorpaySubscriptionId} could not be read from Razorpay: {invoices.Error}");

        var known = await _context.Payments.IgnoreQueryFilters()
            .Where(p => p.ProviderSubscriptionId == mandate.RazorpaySubscriptionId && p.Status == PaymentStatus.Succeeded && p.ProviderPaymentId != null)
            .Select(p => p.ProviderPaymentId!)
            .ToListAsync(cancellationToken);

        var recovered = 0;
        foreach (var invoice in invoices.Value.Where(i => i.Status == "paid" && !string.IsNullOrEmpty(i.PaymentId)).OrderBy(i => i.PaidAt ?? 0))
        {
            if (known.Contains(invoice.PaymentId!))
                continue;

            var payment = new RazorpayWebhookPayment(
                invoice.PaymentId!, invoice.AmountPaid, invoice.Currency, "captured", null, invoice.Id, invoice.OrderId, null,
                RazorpayMandateState.FromUnix(invoice.PaidAt), RazorpayMandateState.FromUnix(invoice.BillingStart), RazorpayMandateState.FromUnix(invoice.BillingEnd));
            await _entitlements.ChargeSucceededAsync(mandate, payment, cancellationToken);
            recovered++;
            _logger.LogWarning(
                "AutoPay reconciliation: payment {PaymentId} on {SubscriptionId} had never been recorded; recovered from invoice {InvoiceId}",
                invoice.PaymentId, mandate.RazorpaySubscriptionId, invoice.Id);
        }

        return recovered;
    }

    // ── Lapsed plans ────────────────────────────────────────────────────────────────────

    /// <summary>The states in which a mandate is not going to pay: a plan resting on one of these ends once what was paid for runs out.
    /// A running mandate is never here - if its payment seems late, that is for reconciliation to look into, not a reason to end the plan.</summary>
    private static readonly RazorpaySubscriptionStatus[] NotPaying =
    {
        RazorpaySubscriptionStatus.Pending, RazorpaySubscriptionStatus.Halted, RazorpaySubscriptionStatus.Paused,
        RazorpaySubscriptionStatus.Cancelled, RazorpaySubscriptionStatus.Completed, RazorpaySubscriptionStatus.Expired
    };

    public async Task<int> EndLapsedPlansAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.UtcNow.AddDays(-AutoPayOptions.GraceDays);
        var mandateIds = await (
                from s in _context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
                join m in _context.RazorpaySubscriptions.IgnoreQueryFilters().AsNoTracking() on s.RazorpaySubscriptionRowId equals m.Id
                where s.CurrentPeriodEndUtc != null && s.CurrentPeriodEndUtc <= cutoff && NotPaying.Contains(m.Status)
                select m.Id)
            .ToListAsync(cancellationToken);

        var ended = 0;
        foreach (var id in mandateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _context.ResetChangeTracker();
            try
            {
                var mandate = await _context.RazorpaySubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
                if (mandate is not null && await _entitlements.EndPaidAccessAsync(mandate, cancellationToken))
                    ended++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AutoPay: the lapsed plan behind mandate {MandateId} could not be ended", id);
            }
        }

        return ended;
    }

    private void Apply(RazorpaySubscription mandate, RazorpaySubscriptionInfo info, DateTime now)
    {
        if (!RazorpayMandateState.Apply(mandate, info, now))
            _logger.LogWarning("Razorpay subscription {SubscriptionId} reported an unknown status '{Status}'; kept {Current}", info.Id, info.Status, mandate.Status);
    }
}
