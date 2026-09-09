namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Mirrors Stripe's own subscription status vocabulary closely enough that
/// StripeWebhookHandler's mapping is a near 1:1 lookup, not a judgment call - see that class for the
/// exact mapping (Stripe has a few statuses, e.g. "incomplete", this collapses into the nearest of
/// these rather than modeling one-for-one).</summary>
public enum SubscriptionStatus
{
    Trialing = 0,
    Active = 1,
    PastDue = 2,
    Canceled = 3
}
