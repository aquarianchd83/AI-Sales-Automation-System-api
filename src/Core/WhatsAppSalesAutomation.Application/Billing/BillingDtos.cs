namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>One row of the public plan catalog - safe to return unauthenticated (GET /billing/plans),
/// so it deliberately excludes IsActive (retired plans are simply omitted from the list, not shown as
/// unavailable - see BillingService.GetPlansAsync). <see cref="PriceMonthlyCents"/> stays the base USD
/// list price; <see cref="CurrencyCode"/>/<see cref="CurrencySymbol"/>/<see cref="LocalPriceAmount"/>
/// are a display/quote figure resolved from RegionalPricingCatalog for whichever country applies to
/// this call (see BillingService.GetPlansAsync's own doc comment).</summary>
public record PlanDto(
    Guid Id,
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int MaxLeadDiscoveryBatchSize,
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
/// has no Subscription row at all yet - still on AuthService.SignUpAsync's trial, never chosen a plan
/// and never had one set any other way either. <paramref name="CurrentPeriodStartUtc"/>/
/// <paramref name="CurrentPeriodEndUtc"/> are both set together by BillingService.ChoosePlanAsync (or
/// left null if the tenant's plan was only ever set via a PlatformSuperAdmin's OverridePlanAsync,
/// which doesn't touch them).</summary>
public record SubscriptionDto(
    Guid? PlanId,
    string? PlanName,
    string Status,
    DateTime? CurrentPeriodStartUtc,
    DateTime? CurrentPeriodEndUtc);

/// <summary>How much of the calling tenant's WhatsApp message quota this calendar month it has used -
/// the read-only counterpart to <see cref="IPlanLimitsService.EnsureCanSendMessageAsync"/>'s own
/// enforcement, backing the tenant Settings page's WhatsApp card. <paramref name="MaxMessagesPerMonth"/>
/// is null when the tenant has no plan yet (still on trial - see IPlanLimitsService's own doc comment)
/// and means unlimited, not zero.</summary>
public record TenantMessageUsageDto(int MessagesSentThisMonth, int? MaxMessagesPerMonth);

/// <summary>One row of the calling tenant's payment history (GET /billing/payments) - see
/// <see cref="Domain.Entities.Billing.Payment"/>'s own doc comment for why every field here is a
/// snapshot at payment time rather than a live join. <paramref name="Provider"/> is "Simulated" for
/// every row today (see BillingService.ChoosePlanAsync) - a real gateway's rows, once one is wired
/// in, carry their own provider name here instead, same shape, no DTO change needed.</summary>
public record PaymentDto(
    Guid Id,
    string PlanName,
    int AmountCents,
    string CurrencyCode,
    string CurrencySymbol,
    decimal LocalAmount,
    string Provider,
    DateTime PaidAtUtc);
