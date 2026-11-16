using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>
/// What a mandate's payments and state changes mean for the tenant: the payment record, the plan and its paid-through date, the wallet
/// credits, the notices. Kept apart from webhook handling on purpose - getting an event safely off the wire and applied once is one job;
/// deciding what the tenant is owed for it is another.
///
/// Every method here may be called more than once for the same thing - a redelivered webhook, a second kind of event about the same
/// payment, a retry after a failure half-way, the reconciliation pass - and must do its work exactly once. A method that throws is
/// retried later; one that returns is taken to have finished.
/// </summary>
public interface IAutoPayEntitlements
{
    /// <summary>Razorpay took a recurring payment on this mandate.</summary>
    Task ChargeSucceededAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default);

    /// <summary>Razorpay tried to take a payment on this mandate and could not. Grants nothing.</summary>
    Task ChargeFailedAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default);

    /// <summary>The mandate moved from <paramref name="previous"/> to its current status - halted, cancelled, paused, running again.</summary>
    Task MandateChangedAsync(RazorpaySubscription mandate, RazorpaySubscriptionStatus previous, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stands in until the wallet-and-billing phase supplies the real one. A payment is refused loudly rather than swallowed: the event that
/// carried it stays Failed in the webhook table and is applied once the real implementation exists, so no payment can be acknowledged and
/// then forgotten. A change of state loses nothing by waiting - the mandate row already holds it - so that one is let through.
/// </summary>
public class AutoPayEntitlementsNotBuilt : IAutoPayEntitlements
{
    public Task ChargeSucceededAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Recording AutoPay payments is not built yet; payment {payment.Id} on {mandate.RazorpaySubscriptionId} stays queued.");

    public Task ChargeFailedAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Recording AutoPay payments is not built yet; failed payment {payment.Id} on {mandate.RazorpaySubscriptionId} stays queued.");

    public Task MandateChangedAsync(RazorpaySubscription mandate, RazorpaySubscriptionStatus previous, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
