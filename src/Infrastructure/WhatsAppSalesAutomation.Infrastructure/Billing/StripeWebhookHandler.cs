using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;
// Aliased: "Subscription" is ambiguous between this and Stripe.Subscription (the Stripe SDK's own
// event payload type, used throughout this file) - every local-entity reference below is written
// TenantSubscription to keep the two visually distinct, not just compiler-distinct.
using TenantSubscription = WhatsAppSalesAutomation.Domain.Entities.Billing.Subscription;

namespace WhatsAppSalesAutomation.Infrastructure.Billing;

/// <summary>
/// Verifies and applies Stripe's own webhook events - the inverse of IBillingService, which only ever
/// starts a Checkout/Portal session; a Subscription is never activated or changed by the API call
/// that started it (Checkout can be abandoned), only by the event Stripe sends once something
/// actually happened. Not itself IStripeWebhookHandler-abstracted behind Application the way
/// IWhatsAppService/IAiService are - like WebhookSignatureValidator, this is pure Stripe-SDK
/// plumbing with no business-logic seam worth hiding, so StripeWebhooksController depends on it
/// directly.
/// </summary>
public interface IStripeWebhookHandler
{
    /// <summary>Verifies <paramref name="signatureHeader"/> against <paramref name="json"/> before
    /// trusting anything in it - throws <see cref="StripeException"/> if verification fails, which
    /// StripeWebhooksController maps to 400 (see its own Receive method).</summary>
    Task HandleEventAsync(string json, string signatureHeader, CancellationToken cancellationToken = default);
}

public class StripeWebhookHandler : IStripeWebhookHandler
{
    private readonly IApplicationDbContext _context;
    private readonly StripeSettings _settings;
    private readonly ILogger<StripeWebhookHandler> _logger;

    public StripeWebhookHandler(IApplicationDbContext context, IOptions<StripeSettings> settings, ILogger<StripeWebhookHandler> logger)
    {
        _context = context;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task HandleEventAsync(string json, string signatureHeader, CancellationToken cancellationToken = default)
    {
        // throwOnApiVersionMismatch: false - Stripe's dashboard-configured account API version can
        // drift from whatever this SDK version shipped against without that being this handler's
        // problem; the shapes this code actually reads (ids, status strings, metadata) are stable
        // across that drift far more often than not.
        var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _settings.WebhookSecret, throwOnApiVersionMismatch: false);

        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutSessionCompletedAsync(stripeEvent, cancellationToken);
                break;
            case "customer.subscription.updated":
                await HandleSubscriptionUpdatedAsync(stripeEvent, cancellationToken);
                break;
            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(stripeEvent, cancellationToken);
                break;
            case "invoice.payment_failed":
                await HandleInvoicePaymentFailedAsync(stripeEvent, cancellationToken);
                break;
            default:
                _logger.LogInformation("Ignored Stripe event {Type} ({Id})", stripeEvent.Type, stripeEvent.Id);
                break;
        }
    }

    /// <summary>Activates the Subscription this Checkout session paid for, and flips the tenant to
    /// Active - this is the one event that turns a trial tenant into a paying one. Reads tenantId/
    /// planId off the session's own Metadata (set by StripeBillingService.CreateCheckoutSessionAsync)
    /// rather than looking the tenant up by StripeCustomerId, since that lookup would fail for a
    /// tenant's very first Checkout (the Customer row was only just created, but no local Subscription
    /// necessarily exists to have recorded it against - EnsureStripeCustomerAsync's own upsert usually
    /// beats the webhook here, but the metadata path never depends on that race resolving one way).</summary>
    private async Task HandleCheckoutSessionCompletedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        if (stripeEvent.Data.Object is not Stripe.Checkout.Session session)
            return;

        if (!Guid.TryParse(session.Metadata?.GetValueOrDefault("tenantId"), out var tenantId))
        {
            _logger.LogWarning("checkout.session.completed {SessionId} had no parseable tenantId in metadata", session.Id);
            return;
        }

        Guid? planId = Guid.TryParse(session.Metadata?.GetValueOrDefault("planId"), out var parsedPlanId) ? parsedPlanId : null;

        var subscription = await _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (subscription is null)
        {
            subscription = new TenantSubscription { TenantId = tenantId };
            _context.Subscriptions.Add(subscription);
        }

        subscription.PlanId = planId;
        subscription.StripeCustomerId = session.CustomerId;
        subscription.StripeSubscriptionId = session.SubscriptionId;
        subscription.Status = SubscriptionStatus.Active;

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is not null)
            tenant.Status = TenantStatus.Active;
        else
            _logger.LogWarning("checkout.session.completed {SessionId} referenced tenant {TenantId}, which no longer exists", session.Id, tenantId);

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Keeps Status/CurrentPeriodEndUtc in sync with Stripe's own record - covers plan
    /// changes, renewals, and Stripe's own retry/recovery out of PastDue (a successful payment on a
    /// previously past_due subscription arrives as this event, not a separate "recovered" one).</summary>
    private async Task HandleSubscriptionUpdatedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        if (stripeEvent.Data.Object is not Stripe.Subscription stripeSubscription)
            return;

        var subscription = await FindByStripeSubscriptionIdAsync(stripeSubscription.Id, cancellationToken);
        if (subscription is null)
        {
            _logger.LogWarning("customer.subscription.updated for {SubscriptionId}, which has no local Subscription row", stripeSubscription.Id);
            return;
        }

        subscription.Status = MapStatus(stripeSubscription.Status);
        // CurrentPeriodEnd moved from Subscription itself onto each SubscriptionItem in Stripe's 2025
        // API redesign (a subscription can hold multiple items with independent billing periods) -
        // Checkout always creates exactly one item per this platform's plans, so the first one's
        // period is the subscription's period as far as this app is concerned.
        subscription.CurrentPeriodEndUtc = stripeSubscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd;

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Stripe's own hard cancellation (the tenant canceled, or dunning finally gave up) -
    /// distinct from invoice.payment_failed's soft PastDue, this is the point access actually stops.</summary>
    private async Task HandleSubscriptionDeletedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        if (stripeEvent.Data.Object is not Stripe.Subscription stripeSubscription)
            return;

        var subscription = await FindByStripeSubscriptionIdAsync(stripeSubscription.Id, cancellationToken);
        if (subscription is null)
        {
            _logger.LogWarning("customer.subscription.deleted for {SubscriptionId}, which has no local Subscription row", stripeSubscription.Id);
            return;
        }

        subscription.Status = SubscriptionStatus.Canceled;

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == subscription.TenantId, cancellationToken);
        if (tenant is not null)
            tenant.Status = TenantStatus.Cancelled;

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A failed renewal charge - flips the Subscription to PastDue only, deliberately NOT the
    /// Tenant itself (see AuthService.LoginAsync's suspended/cancelled guard): a standard dunning
    /// grace period, not immediate suspension. Stripe retries the charge on its own schedule and
    /// either recovers (customer.subscription.updated back to Active) or eventually gives up
    /// (customer.subscription.deleted) - this event alone never suspends anyone.</summary>
    private async Task HandleInvoicePaymentFailedAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        if (stripeEvent.Data.Object is not Invoice invoice)
            return;

        // Invoice.SubscriptionId moved under Invoice.Parent.SubscriptionDetails in the same API
        // redesign as SubscriptionItem's period fields - see HandleSubscriptionUpdatedAsync's comment.
        var stripeSubscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
        if (stripeSubscriptionId is null)
            return;

        var subscription = await FindByStripeSubscriptionIdAsync(stripeSubscriptionId, cancellationToken);
        if (subscription is null)
        {
            _logger.LogWarning("invoice.payment_failed for subscription {SubscriptionId}, which has no local Subscription row", stripeSubscriptionId);
            return;
        }

        subscription.Status = SubscriptionStatus.PastDue;
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The cross-tenant-by-design lookup every event handler above needs: Stripe's webhook
    /// carries its own subscription id, not this platform's TenantId, and there is no tenant in scope
    /// yet to filter by (this whole controller is anonymous) - same reasoning as
    /// TenantWhatsAppConfigProvider.GetByPhoneNumberIdAsync.</summary>
    private Task<TenantSubscription?> FindByStripeSubscriptionIdAsync(string stripeSubscriptionId, CancellationToken cancellationToken) =>
        _context.Subscriptions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId, cancellationToken);

    private static SubscriptionStatus MapStatus(string stripeStatus) => stripeStatus switch
    {
        "trialing" => SubscriptionStatus.Trialing,
        "active" => SubscriptionStatus.Active,
        "past_due" or "incomplete" or "unpaid" or "paused" => SubscriptionStatus.PastDue,
        "canceled" or "incomplete_expired" => SubscriptionStatus.Canceled,
        _ => SubscriptionStatus.PastDue
    };
}
