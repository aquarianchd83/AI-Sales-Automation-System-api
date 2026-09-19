namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Refund rules (Billing:Refunds). Days are whole days; the usage fraction is 0-1 (0.10 = 10%).</summary>
public record RefundPolicyConfigDto(
    int CreditWindowDays,
    int SubscriptionWindowDays,
    decimal SubscriptionMaxUsageFraction,
    int SubscriptionPeriodDays,
    int RequestExpiryDays);

/// <summary>How billing alerts reach a tenant on WhatsApp (Billing:Alerts): the approved Utility template's name and language.</summary>
public record BillingAlertConfigDto(string WhatsAppTemplateName, string WhatsAppTemplateLanguage);

/// <summary>The free quota a trial tenant gets (Billing:Trial).</summary>
public record TrialQuotaConfigDto(decimal WhatsAppMessages, decimal AiConversations, decimal LeadCandidates);

/// <summary>How many pooled WhatsApp quota units one template send of each category uses (WhatsApp:QuotaWeights).</summary>
public record QuotaWeightConfigDto(decimal Marketing, decimal Authentication, decimal Utility);

/// <summary>What one WhatsApp template message of each category is charged at, in USD.</summary>
public record WhatsAppCategoryRatesDto(decimal Marketing, decimal Utility, decimal Authentication);

/// <summary>A country's WhatsApp rates. <see cref="CountryName"/> is display-only and ignored on save.</summary>
public record WhatsAppCountryRatesDto(string CountryCode, string CountryName, decimal Marketing, decimal Utility, decimal Authentication);

public record WhatsAppChargesConfigDto(WhatsAppCategoryRatesDto Default, IReadOnlyList<WhatsAppCountryRatesDto> Countries);

/// <summary>Per-million-token rates for a lead discovery model, in USD. <see cref="Model"/> is ignored for the default row.</summary>
public record LeadDiscoveryModelRatesDto(string Model, decimal InputPerMillion, decimal OutputPerMillion, decimal CacheReadPerMillion, decimal CacheWritePerMillion);

public record LeadDiscoveryChargesConfigDto(
    decimal WebSearchPerThousand,
    LeadDiscoveryModelRatesDto Default,
    IReadOnlyList<LeadDiscoveryModelRatesDto> Models,
    // The model the platform runs; a run on a model with no row is priced at this one. Null = no choice made.
    string? DefaultModel = null);

/// <summary>Per-1,000-token rates for a conversational AI model, in USD. Model reads "Provider:model".</summary>
public record AiModelRatesDto(string Model, decimal PromptPer1K, decimal CompletionPer1K);

/// <summary><see cref="DefaultModel"/> reads "Provider:model" and must be one of <see cref="Models"/>. Null = no choice made.</summary>
public record AiChargesConfigDto(IReadOnlyList<AiModelRatesDto> Models, string? DefaultModel = null);

/// <summary>What the platform charges (or estimates) for each metered thing, all in USD.</summary>
public record ChargesConfigDto(WhatsAppChargesConfigDto WhatsApp, LeadDiscoveryChargesConfigDto LeadDiscovery, AiChargesConfigDto Ai);

/// <summary>One country the platform prices for. Switched off, it is offered nowhere in the application; tenants
/// already in it are unaffected.</summary>
public record CountryConfigDto(string CountryCode, string CountryName, string CurrencyCode, string CurrencySymbol, bool IsEnabled);

/// <summary>Everything on the Platform Admin Console's Configuration page. The same shape is read and written: a save
/// replaces the whole document, so what the operator sees is exactly what is stored.</summary>
public record PlatformConfigurationDto(
    RefundPolicyConfigDto Refunds,
    BillingAlertConfigDto Alerts,
    TrialQuotaConfigDto Trial,
    QuotaWeightConfigDto QuotaWeights,
    ChargesConfigDto Charges,
    // Every country the platform prices for, on or off. Null on a save leaves the choice as it is.
    IReadOnlyList<CountryConfigDto>? Countries = null);
