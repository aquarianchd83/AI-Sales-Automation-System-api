using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One tenant's billing relationship with the platform - unlike <see cref="Plan"/>, this IS
/// <see cref="ITenantOwned"/>. Exactly one row per tenant in practice (BillingService.ChoosePlanAsync
/// upserts by TenantId rather than always inserting), not a full subscription-change history - see
/// <see cref="Payment"/> for that; this is just "what does this tenant's access look like right now."
/// Provider-agnostic on purpose: it used to carry Stripe's own Customer/Subscription ids, removed
/// once Stripe was pulled out in favor of a simulated flow (see BillingService.ChoosePlanAsync's own
/// doc comment) - a real gateway's own ids, whenever one is wired in, belong on <see cref="Payment"/>
/// instead, not reintroduced here.
/// </summary>
public class Subscription : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Null until the tenant chooses a plan at least once - see
    /// AuthService.SignUpAsync's own trial handling for how a brand-new tenant behaves before this
    /// exists at all.</summary>
    public Guid? PlanId { get; set; }

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trialing;

    /// <summary>When the current billing period (and so the current plan) started - set by
    /// BillingService.ChoosePlanAsync to the moment the (simulated) payment was made. Null until the
    /// tenant has chosen a plan at least once, same as CurrentPeriodEndUtc.</summary>
    public DateTime? CurrentPeriodStartUtc { get; set; }

    public DateTime? CurrentPeriodEndUtc { get; set; }
}
