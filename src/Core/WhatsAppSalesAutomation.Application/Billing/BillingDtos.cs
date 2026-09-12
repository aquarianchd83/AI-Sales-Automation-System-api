namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>One row of the public plan catalog - safe to return unauthenticated (GET /billing/plans),
/// so it deliberately excludes StripePriceId (an internal detail, not a secret, but not this DTO's
/// business either) and IsActive (retired plans are simply omitted from the list, not shown as
/// unavailable - see BillingService.GetPlansAsync). <see cref="PriceMonthlyCents"/> stays the base USD
/// price Stripe actually charges; <see cref="CurrencyCode"/>/<see cref="CurrencySymbol"/>/
/// <see cref="LocalPriceAmount"/> are a display/quote figure resolved from
/// RegionalPricingCatalog for whichever country applies to this call (see
/// StripeBillingService.GetPlansAsync's own doc comment) - not what Stripe bills.</summary>
public record PlanDto(
    Guid Id,
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents,
    string CurrencyCode,
    string CurrencySymbol,
    decimal LocalPriceAmount);

/// <summary>One row of the public region catalog (GET /billing/regions, no auth required) - what the
/// signup page's country picker renders. See RegionalPricingCatalog's own doc comment for why this is
/// a small hand-maintained list rather than a DB table.</summary>
public record RegionDto(string CountryCode, string CountryName, string CurrencyCode, string CurrencySymbol);

/// <summary>The calling tenant's current billing state - <paramref name="Status"/> is the string form
/// of SubscriptionStatus. Null (the whole DTO, from GetSubscriptionForTenantAsync) means the tenant
/// has no Subscription row at all yet - still on AuthService.SignUpAsync's trial, never completed
/// Checkout and never had a plan set any other way either. A non-null DTO does NOT by itself mean a
/// Stripe customer exists though - a PlatformSuperAdmin's OverridePlanAsync creates/updates this same
/// row directly, with no Stripe involved at all - see <paramref name="HasStripeCustomer"/>, the exact
/// condition CreateBillingPortalSessionAsync itself requires; a tenant whose plan was only ever set
/// that way has no Billing Portal to open yet ("This tenant has no Stripe customer yet - complete
/// Checkout first" is CreateBillingPortalSessionAsync's own error for calling it anyway).
/// <paramref name="CurrentPeriodStartUtc"/>/<paramref name="CurrentPeriodEndUtc"/> are both null until
/// the first customer.subscription.updated webhook lands (see
/// StripeWebhookHandler.HandleSubscriptionUpdatedAsync) - a beat after Checkout completes, not
/// simultaneous with it, and never at all for an admin-overridden plan.</summary>
public record SubscriptionDto(
    Guid? PlanId,
    string? PlanName,
    string Status,
    DateTime? CurrentPeriodStartUtc,
    DateTime? CurrentPeriodEndUtc,
    bool HasStripeCustomer);

/// <summary><paramref name="SuccessUrl"/>/<paramref name="CancelUrl"/> are the frontend's own routes
/// to redirect back to - this API has no opinion on the frontend's URL structure, so the caller
/// supplies both rather than either being hardcoded or config-driven.</summary>
public record CreateCheckoutSessionRequest(Guid PlanId, string SuccessUrl, string CancelUrl);

public record CreateBillingPortalSessionRequest(string ReturnUrl);

/// <summary>The URL to redirect the browser to - Stripe Checkout/the Billing Portal are both
/// Stripe-hosted pages, not something this API renders itself.</summary>
public record BillingSessionUrlDto(string Url);

/// <summary>How much of the calling tenant's WhatsApp message quota this calendar month it has used -
/// the read-only counterpart to <see cref="IPlanLimitsService.EnsureCanSendMessageAsync"/>'s own
/// enforcement, backing the tenant Settings page's WhatsApp card. <paramref name="MaxMessagesPerMonth"/>
/// is null when the tenant has no plan yet (still on trial - see IPlanLimitsService's own doc comment)
/// and means unlimited, not zero.</summary>
public record TenantMessageUsageDto(int MessagesSentThisMonth, int? MaxMessagesPerMonth);
