using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform-only view of the plan catalog - unlike the tenant-facing <c>PlanDto</c>
/// (Application.Billing), this includes <see cref="IsActive"/> since a PlatformSuperAdmin manages the
/// catalog itself rather than just picking from it.</summary>
public record PlatformPlanDto(
    Guid Id,
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int MaxLeadDiscoveryBatchSize,
    // The authored base list price, always USD - what the catalog is stored and edited in.
    int PriceMonthlyCents,
    bool IsActive,
    // The same price quoted in the SIGNED-IN OPERATOR's currency (from their profile country), so the
    // catalog reads in the money they think in. Display only: PriceMonthlyCents stays the stored value and
    // the one a create/update request carries, so a rate change never rewrites a plan.
    string CurrencyCode,
    string CurrencySymbol,
    decimal PriceMonthlyLocal,
    // What the plan includes each billing period, per prepaid quota type. A type the plan doesn't include is
    // simply absent. Changing these affects the NEXT allocation a tenant on the plan receives, never one already given.
    IReadOnlyList<IncludedQuotaDto> IncludedQuotas,
    // Explicit prices set per country, each in that country's own currency. A country not listed here is quoted
    // the base USD price converted at the catalog rate.
    IReadOnlyList<CountryPriceDto> CountryPrices);

/// <summary>An explicit price for one country, in that country's currency.</summary>
public record CountryPriceDto(string CountryCode, string CountryName, string CurrencyCode, string CurrencySymbol, decimal Amount);

/// <summary>A country price being set. An amount of 0 removes that country's price (it falls back to the converted USD price).</summary>
public record CountryPriceInput(string CountryCode, decimal Amount);

/// <summary>One quota a plan includes. Units of 0 mean "none of this", and remove the row.</summary>
public record PlanQuotaInput(Domain.Enums.QuotaType QuotaType, decimal Units);

/// <summary>One row of the Subscriptions & Billing screen (spec item #3). <see cref="HasFailedPayment"/>
/// is derived from <see cref="SubscriptionStatus.PastDue"/> - it means "this tenant's subscription is
/// currently past due" (payments themselves are simulated for now - see
/// Application.Billing.IBillingService's own doc comment). Billing is prepaid, so there are no invoices to
/// be late on: what a tenant has paid is on the Payments screen.</summary>
public record PlatformSubscriptionListItemDto(
    Guid TenantId,
    string TenantName,
    string? PlanName,
    SubscriptionStatus? Status,
    DateTime? CurrentPeriodEndUtc,
    bool HasFailedPayment);

public record PlatformSubscriptionQuery : PagedRequest
{
    public SubscriptionStatus? Status { get; init; }
}

/// <summary>Body of POST the plan catalog endpoint. <see cref="Code"/> is immutable once a plan
/// exists (see Plan.Code's own doc comment) - there is no update path for it, only creation.</summary>
public record CreatePlanRequest(
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents,
    int? MaxLeadDiscoveryBatchSize = null,
    IReadOnlyList<PlanQuotaInput>? IncludedQuotas = null,
    IReadOnlyList<CountryPriceInput>? CountryPrices = null);

/// <summary>Body of PUT one plan. <see cref="IsActive"/> is how a plan is both retired ("Delete" in
/// the admin UI sets it false) and un-retired - there is no separate reactivate endpoint.</summary>
public record UpdatePlanRequest(
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents,
    bool IsActive,
    int? MaxLeadDiscoveryBatchSize = null,
    // Null leaves the plan's quotas as they are; a list is the complete new set.
    IReadOnlyList<PlanQuotaInput>? IncludedQuotas = null,
    // Null leaves the plan's country prices as they are; a list is the complete new set.
    IReadOnlyList<CountryPriceInput>? CountryPrices = null);

/// <summary>One credit pack in the catalog, as the operator manages it. PriceCents is the authored base USD price;
/// the local price is a display conversion into the signed-in operator's currency, like <see cref="PlatformPlanDto"/>.</summary>
public record PlatformCreditPackDto(
    Guid Id,
    Domain.Enums.QuotaType QuotaType,
    string Name,
    decimal Units,
    int PriceCents,
    bool IsActive,
    string CurrencyCode,
    string CurrencySymbol,
    decimal PriceLocal,
    IReadOnlyList<CountryPriceDto> CountryPrices);

/// <summary>QuotaType is fixed once a pack exists - a pack of WhatsApp messages never becomes a pack of AI conversations.</summary>
public record CreateCreditPackRequest(Domain.Enums.QuotaType QuotaType, string Name, decimal Units, int PriceCents,
    IReadOnlyList<CountryPriceInput>? CountryPrices = null);

public record UpdateCreditPackRequest(string Name, decimal Units, int PriceCents, bool IsActive,
    // Null leaves the pack's country prices as they are; a list is the complete new set.
    IReadOnlyList<CountryPriceInput>? CountryPrices = null);
