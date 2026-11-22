using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>
/// Makes the tenant's plan follow what Razorpay has actually been paid on a mandate, whatever happened to the webhooks. Used by the daily
/// reconciliation and by "Check status" on the Billing page, so a tenant who has just authorised and paid does not have to wait for a
/// webhook (or for tomorrow's reconciliation) to get the plan they paid for.
/// <list type="number">
/// <item>Payments Razorpay counts but that are not on record here (a <c>subscription.charged</c> that never arrived, or never applied)
/// are read from the mandate's paid invoices and handed to <see cref="IAutoPayEntitlements.ChargeSucceededAsync"/>, which records each
/// once and moves the tenant onto the plan.</item>
/// <item>A payment that IS on record, for a paid period still running, while the tenant's subscription is not on that mandate's plan, is
/// handed to the same method again. It records nothing twice and grants no credit twice; it only puts the plan back where the payment says
/// it belongs.</item>
/// </list>
/// Nothing here starts a debit.
/// </summary>
public static class AutoPayPaymentSync
{
    /// <returns>How many payments were recovered from Razorpay's invoices, and whether the tenant's plan had to be put back on the mandate.</returns>
    public static async Task<(int Recovered, bool Rebound)> SyncAsync(
        IApplicationDbContext context,
        IRazorpayGateway gateway,
        IAutoPayEntitlements entitlements,
        IDateTimeProvider clock,
        ILogger logger,
        RazorpaySubscription mandate,
        RazorpayCredentials credentials,
        int razorpayPaidCount,
        CancellationToken cancellationToken = default)
    {
        var recovered = 0;
        if (razorpayPaidCount > await RecordedPaymentsAsync(context, mandate.RazorpaySubscriptionId, cancellationToken))
            recovered = await RecoverPaymentsAsync(context, gateway, entitlements, logger, mandate, credentials, cancellationToken);

        var rebound = await RebindPlanAsync(context, entitlements, clock, logger, mandate, cancellationToken);
        return (recovered, rebound);
    }

    public static Task<int> RecordedPaymentsAsync(IApplicationDbContext context, string razorpaySubscriptionId, CancellationToken cancellationToken) =>
        context.Payments.IgnoreQueryFilters()
            .CountAsync(p => p.ProviderSubscriptionId == razorpaySubscriptionId && p.Status == PaymentStatus.Succeeded && p.Kind == PaymentKind.Subscription, cancellationToken);

    /// <summary>
    /// Records the payments Razorpay took that never reached us. Razorpay raises an invoice for each billing cycle and a paid one names its
    /// payment, so each paid invoice whose payment is not on record is handed to the same code a charge webhook reaches - which records a
    /// payment and grants its credits exactly once, so a payment whose webhook is merely late, and arrives afterwards, is not counted twice.
    /// </summary>
    private static async Task<int> RecoverPaymentsAsync(
        IApplicationDbContext context, IRazorpayGateway gateway, IAutoPayEntitlements entitlements, ILogger logger,
        RazorpaySubscription mandate, RazorpayCredentials credentials, CancellationToken cancellationToken)
    {
        var invoices = await gateway.FetchSubscriptionInvoicesAsync(credentials, mandate.RazorpaySubscriptionId, cancellationToken);
        if (!invoices.Success || invoices.Value is null)
            throw new InvalidOperationException($"The invoices of {mandate.RazorpaySubscriptionId} could not be read from Razorpay: {invoices.Error}");

        var known = await context.Payments.IgnoreQueryFilters()
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
            await entitlements.ChargeSucceededAsync(mandate, payment, cancellationToken);
            recovered++;
            logger.LogWarning(
                "AutoPay: payment {PaymentId} on {SubscriptionId} had never been recorded; recovered from invoice {InvoiceId}",
                invoice.PaymentId, mandate.RazorpaySubscriptionId, invoice.Id);
        }

        return recovered;
    }

    /// <summary>
    /// A mandate that is still paying, with a payment on record whose period has not run out, must be what the tenant's plan rests on. When
    /// it is not - the subscription points at another plan or at no mandate - the latest such payment is applied again, which only sets the
    /// plan and the paid-through date (the payment row and its credits already exist and are not added twice).
    /// </summary>
    private static async Task<bool> RebindPlanAsync(
        IApplicationDbContext context, IAutoPayEntitlements entitlements, IDateTimeProvider clock, ILogger logger,
        RazorpaySubscription mandate, CancellationToken cancellationToken)
    {
        if (mandate.Status is not (RazorpaySubscriptionStatus.Active or RazorpaySubscriptionStatus.Authenticated))
            return false;

        var subscription = await context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == mandate.TenantId, cancellationToken);
        if (subscription is not null && subscription.PlanId == mandate.PlanId && subscription.RazorpaySubscriptionRowId == mandate.Id
            && subscription.Status == SubscriptionStatus.Active)
            return false;

        var now = clock.UtcNow;
        var paid = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ProviderSubscriptionId == mandate.RazorpaySubscriptionId && p.Status == PaymentStatus.Succeeded
                        && p.Kind == PaymentKind.Subscription && p.ProviderPaymentId != null)
            .OrderByDescending(p => p.PaidAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (paid is null)
            return false;

        var periodEnd = paid.PeriodEndUtc ?? mandate.CurrentEndUtc ?? paid.PaidAtUtc.AddMonths(1);
        if (periodEnd <= now)
            return false;

        logger.LogWarning(
            "AutoPay: mandate {SubscriptionId} is {Status} and paid ({PaymentId}), but tenant {TenantId} was on plan {CurrentPlan} (mandate {CurrentMandate}); putting it back on {PlanId}",
            mandate.RazorpaySubscriptionId, mandate.Status, paid.ProviderPaymentId, mandate.TenantId, subscription?.PlanId, subscription?.RazorpaySubscriptionRowId, mandate.PlanId);

        // The amount and currency are the mandate's own: this payment was recorded against it, so it already passed that check once.
        var payment = new RazorpayWebhookPayment(
            paid.ProviderPaymentId!, mandate.AmountMinor, mandate.Currency, "captured", paid.PaymentMethod, paid.ProviderInvoiceId, null, null,
            paid.PaidAtUtc, paid.PeriodStartUtc, paid.PeriodEndUtc);
        await entitlements.ChargeSucceededAsync(mandate, payment, cancellationToken);
        return true;
    }
}
