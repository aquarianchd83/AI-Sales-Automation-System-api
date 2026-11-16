using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>
/// What a mandate's payments and state changes mean for the tenant: the payment record, the plan and its paid-through date, the wallet
/// credits, the notices. Kept apart from webhook handling on purpose - getting an event safely off the wire and applied once is one job;
/// deciding what the tenant is owed for it is another.
///
/// Every method here may be called more than once for the same thing - a redelivered webhook, a second kind of event about the same
/// payment, a retry after a failure half-way, the reconciliation pass, two workers at the same moment - and must do its work exactly
/// once. A method that throws is retried later; one that returns is taken to have finished.
/// </summary>
public interface IAutoPayEntitlements
{
    /// <summary>Razorpay took a recurring payment on this mandate.</summary>
    Task ChargeSucceededAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default);

    /// <summary>Razorpay tried to take a payment on this mandate and could not. Grants nothing.</summary>
    Task ChargeFailedAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default);

    /// <summary>The mandate moved from <paramref name="previous"/> to its current status - halted, cancelled, paused, running again.</summary>
    Task MandateChangedAsync(RazorpaySubscription mandate, RazorpaySubscriptionStatus previous, CancellationToken cancellationToken = default);

    /// <summary>The mandate has stopped paying and what was paid for (and the grace after it) is over: the tenant leaves the paid plan.
    /// Does nothing when this mandate is not the one behind the tenant's plan.</summary>
    /// <returns>Whether a plan was ended.</returns>
    Task<bool> EndPaidAccessAsync(RazorpaySubscription mandate, CancellationToken cancellationToken = default);
}
