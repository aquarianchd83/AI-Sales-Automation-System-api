namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// Tenant-facing billing operations, backed by Stripe - implemented in Infrastructure
/// (StripeBillingService) since it's the only thing in this codebase that talks to Stripe's API
/// directly, same layering as IWhatsAppService/IAiService hiding their real providers from
/// Application. Deliberately thin: Stripe's own Checkout and Customer Portal are Stripe-hosted pages
/// this API only ever redirects to, never renders - far less to build and secure than a custom
/// billing-management UI, at the cost of Stripe's own branding on those pages.
/// </summary>
public interface IBillingService
{
    /// <summary>The public plan catalog - every active Plan, safe to call unauthenticated.</summary>
    Task<IReadOnlyList<PlanDto>> GetPlansAsync(CancellationToken cancellationToken = default);

    /// <summary>The calling tenant's current billing state - null if it has never completed
    /// Checkout (see SubscriptionDto's own doc comment).</summary>
    Task<SubscriptionDto?> GetSubscriptionForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Starts a Stripe Checkout session for one plan - creates a Stripe Customer for this
    /// tenant on first use (see Subscription.StripeCustomerId's own doc comment), reused on every
    /// later call. Returns the URL to redirect the browser to; the resulting Subscription row is not
    /// activated here - that happens when Stripe's checkout.session.completed webhook arrives (see
    /// StripeWebhookHandler), since Checkout itself can be abandoned.</summary>
    Task<string> CreateCheckoutSessionAsync(Guid tenantId, Guid planId, string successUrl, string cancelUrl, CancellationToken cancellationToken = default);

    /// <summary>Starts a session for Stripe's own Customer Portal (plan changes, payment method,
    /// invoice history, cancellation) - requires the tenant to already have a StripeCustomerId
    /// (i.e., to have completed Checkout at least once).</summary>
    Task<string> CreateBillingPortalSessionAsync(Guid tenantId, string returnUrl, CancellationToken cancellationToken = default);
}
