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
    decimal LocalPriceAmount,
    IReadOnlyList<IncludedQuotaDto> IncludedQuotas,
    // Tax added on top of LocalPriceAmount for the tenant this is quoted to, and what they would pay in all.
    IReadOnlyList<TaxLineDto> TaxLines,
    decimal TaxLocal,
    decimal TotalLocal);

/// <summary>Units of one quota type a plan includes each billing period.</summary>
public record IncludedQuotaDto(Domain.Enums.QuotaType QuotaType, decimal Units);

/// <summary>One purchasable credit pack, priced in the calling tenant's own currency (PriceCents stays base USD).</summary>
public record CreditPackDto(
    Guid Id,
    Domain.Enums.QuotaType QuotaType,
    string Name,
    decimal Units,
    int PriceCents,
    string CurrencyCode,
    string CurrencySymbol,
    decimal LocalPriceAmount,
    IReadOnlyList<TaxLineDto> TaxLines,
    decimal TaxLocal,
    decimal TotalLocal);

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

/// <summary>One template category's share of a tenant's WhatsApp charges. <paramref name="Category"/> is a
/// TemplateCategory name (Marketing/Utility/Authentication).</summary>
public record TenantWhatsAppCategoryChargeDto(
    string Category,
    int Messages,
    decimal RatePerMessageUsd,
    decimal EstimatedCostUsd,
    decimal EstimatedCostLocal);

/// <summary>What the tenant's WhatsApp sending cost this month. <paramref name="MessagesSent"/> is every
/// message in the period (the population the plan allowance is measured against);
/// <paramref name="BillableMessages"/> is the template sends Meta actually charges for - an inbound message
/// or a free-form session reply costs nothing.</summary>
public record TenantWhatsAppChargesDto(
    int MessagesSent,
    int BillableMessages,
    int FreeMessages,
    decimal EstimatedCostUsd,
    decimal EstimatedCostLocal,
    IReadOnlyList<TenantWhatsAppCategoryChargeDto> ByCategory);

/// <summary>What the tenant's lead discovery runs cost this month - the current-month half of what
/// GET /lead-discovery/spend reports, repeated here so one call fills the Settings page.</summary>
public record TenantLeadDiscoveryChargesDto(
    int Runs,
    int LeadsSaved,
    decimal EstimatedCostUsd,
    decimal EstimatedCostLocal);

/// <summary>This calendar month's usage charges for the calling tenant: WhatsApp sending plus lead
/// discovery. Excludes the plan subscription fee, which is not usage. Every figure is an ESTIMATE priced
/// from hand-maintained rate tables - see WhatsAppPricingOptions and LeadDiscoveryPricingOptions - so
/// whatever renders this must say so rather than presenting it as a bill.</summary>
public record TenantChargesDto(
    string CurrencyCode,
    string CurrencySymbol,
    DateTime PeriodStartUtc,
    TenantWhatsAppChargesDto WhatsApp,
    TenantLeadDiscoveryChargesDto LeadDiscovery,
    decimal TotalEstimatedCostUsd,
    decimal TotalEstimatedCostLocal);

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
    DateTime PaidAtUtc,
    string Kind,
    // LocalAmount is the price before tax; the tenant paid TotalLocal. A refund row carries all of these negative.
    string? CountryCode = null,
    decimal TaxLocal = 0,
    decimal TotalLocal = 0,
    IReadOnlyList<TaxLineDto>? TaxLines = null,
    decimal AmountInr = 0)
{
    public static PaymentDto From(Domain.Entities.Billing.Payment p) => new(
        p.Id, p.PlanName, p.AmountCents, p.CurrencyCode, p.CurrencySymbol, p.LocalAmount, p.Provider, p.PaidAtUtc, p.Kind.ToString(),
        p.CountryCode, p.TaxLocal, p.TotalPaidLocal, ParseTaxLines(p.TaxLinesJson), p.AmountInr);

    public static IReadOnlyList<TaxLineDto> ParseTaxLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<TaxLineDto>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<TaxLineDto>>(json) ?? new List<TaxLineDto>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<TaxLineDto>();
        }
    }
}
