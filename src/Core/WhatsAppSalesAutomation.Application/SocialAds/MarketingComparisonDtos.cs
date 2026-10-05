namespace WhatsAppSalesAutomation.Application.SocialAds;

/// <summary>
/// Social media advertising against WhatsApp automation, over the Revenue report's period. Per-sale figures and
/// <c>ReturnOnSpend</c> (revenue earned per unit spent) are null when they cannot be worked out honestly: no sales or
/// no spend yet, or ad spend in a different currency from the tenant's.
/// </summary>
public record MarketingComparisonDto(
    int Months,
    string SpendSource,
    string ConnectionStatus,
    string? SpendCurrencyCode,
    string TenantCurrencyCode,
    bool CurrencyMatches,
    decimal Revenue,
    int Sales,
    SocialChannelDto Social,
    WhatsAppChannelDto WhatsApp,
    string? CheaperChannel,
    decimal? SavingsPerSale,
    IReadOnlyList<SocialPlatformDto> Platforms,
    IReadOnlyList<SpendTrendPointDto> SpendTrend);

public record SocialChannelDto(
    decimal Spend,
    long Impressions,
    long Clicks,
    int Leads,
    decimal? CostPerClick,
    decimal? CostPerLead,
    decimal? CostPerSale,
    double? ReturnOnSpend);

/// <summary>The platform's estimated cost of WhatsApp sending plus lead discovery in the period - an estimate, not a bill.</summary>
public record WhatsAppChannelDto(decimal Cost, int MessagesSent, decimal? CostPerSale, double? ReturnOnSpend);

public record SocialPlatformDto(string Platform, decimal Spend, double? SharePercent, long Clicks, int Leads);

public record SpendTrendPointDto(DateTime Start, decimal Spend);
