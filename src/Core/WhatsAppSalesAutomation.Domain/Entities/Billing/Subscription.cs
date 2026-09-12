using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One tenant's billing relationship with Stripe - unlike <see cref="Plan"/>, this IS
/// <see cref="ITenantOwned"/>. Exactly one row per tenant in practice (StripeWebhookHandler
/// upserts by TenantId rather than always inserting), not a full subscription-change history -
/// Stripe itself is the system of record for history; this is just "what does this tenant's access
/// look like right now."
/// </summary>
public class Subscription : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Null until the tenant completes Checkout at least once - see
    /// AuthService.SignUpAsync's own trial handling for how a brand-new tenant behaves before this
    /// exists at all.</summary>
    public Guid? PlanId { get; set; }

    /// <summary>Stripe's Customer id - created the first time this tenant starts a Checkout session,
    /// reused for every subsequent one and for the Billing Portal, so Stripe sees one customer per
    /// tenant rather than a new one per checkout attempt.</summary>
    public string? StripeCustomerId { get; set; }

    public string? StripeSubscriptionId { get; set; }

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trialing;

    /// <summary>When the current billing period (and so the current plan) started - set alongside
    /// <see cref="CurrentPeriodEndUtc"/> from Stripe's own subscription item (see
    /// StripeWebhookHandler.HandleSubscriptionUpdatedAsync). Null until the first
    /// customer.subscription.updated event arrives, same as CurrentPeriodEndUtc.</summary>
    public DateTime? CurrentPeriodStartUtc { get; set; }

    public DateTime? CurrentPeriodEndUtc { get; set; }
}
