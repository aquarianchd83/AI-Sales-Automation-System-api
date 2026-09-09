namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>One row of the public plan catalog - safe to return unauthenticated (GET /billing/plans),
/// so it deliberately excludes StripePriceId (an internal detail, not a secret, but not this DTO's
/// business either) and IsActive (retired plans are simply omitted from the list, not shown as
/// unavailable - see BillingService.GetPlansAsync).</summary>
public record PlanDto(
    Guid Id,
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents);

/// <summary>The calling tenant's current billing state - <paramref name="Status"/> is the string form
/// of SubscriptionStatus. Null (the whole DTO, from GetSubscriptionForTenantAsync) means the tenant
/// has never completed Checkout - still on AuthService.SignUpAsync's trial, not yet a Stripe
/// customer at all.</summary>
public record SubscriptionDto(
    Guid? PlanId,
    string? PlanName,
    string Status,
    DateTime? CurrentPeriodEndUtc);

/// <summary><paramref name="SuccessUrl"/>/<paramref name="CancelUrl"/> are the frontend's own routes
/// to redirect back to - this API has no opinion on the frontend's URL structure, so the caller
/// supplies both rather than either being hardcoded or config-driven.</summary>
public record CreateCheckoutSessionRequest(Guid PlanId, string SuccessUrl, string CancelUrl);

public record CreateBillingPortalSessionRequest(string ReturnUrl);

/// <summary>The URL to redirect the browser to - Stripe Checkout/the Billing Portal are both
/// Stripe-hosted pages, not something this API renders itself.</summary>
public record BillingSessionUrlDto(string Url);
