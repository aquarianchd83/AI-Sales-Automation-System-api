using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>What one template message of each category costs to send, in USD.</summary>
public class WhatsAppCategoryRates
{
    public decimal Marketing { get; set; }

    public decimal Utility { get; set; }

    public decimal Authentication { get; set; }

    public decimal For(TemplateCategory category) => category switch
    {
        TemplateCategory.Utility => Utility,
        TemplateCategory.Authentication => Authentication,
        _ => Marketing
    };
}

/// <summary>
/// Bound from "WhatsApp:Pricing". What sending a WhatsApp template message is charged at, used to turn the
/// messages this platform has sent into the money figure a tenant (and the platform operator) sees - see
/// Billing.WhatsAppSpendService.
///
/// Two things to know before trusting a figure derived from this:
///
/// 1. These are list prices copied by hand, exactly like Billing.RegionalPricingCatalog and
///    LeadDiscoveryPricingOptions, with the same caveat: they can lag a Meta price change, so every figure
///    is an ESTIMATE and Meta's own invoice is the authority. Re-check them against Meta's current rate
///    card before anyone treats these numbers as billing.
/// 2. Meta prices per message by template category, and prices by the RECIPIENT's country. This platform
///    stores no country per customer, so <see cref="RatesFor"/> is given the TENANT's country instead -
///    right for the common case of a business messaging its own market, wrong for one messaging abroad.
///    A per-recipient figure would need the customer's country derived from their E.164 number.
///
/// Service/session messages (a plain text reply inside an open conversation) are not priced here: they are
/// free under per-message pricing, which is why WhatsAppSpendService counts only template sends.
/// </summary>
public class WhatsAppPricingOptions
{
    /// <summary>Applied to a country with no entry below. Deliberately not the cheapest row - an unpriced
    /// country should not look free; see <see cref="Countries"/> on why over- beats under-stating here.</summary>
    public WhatsAppCategoryRates Default { get; set; } =
        new() { Marketing = 0.0250m, Utility = 0.0040m, Authentication = 0.0135m };

    /// <summary>
    /// Keyed by ISO country code, the same codes Billing.RegionalPricingCatalog lists, so a tenant priced in
    /// a currency also has a message rate. Configuration merges into these defaults by key, so one rate can
    /// be corrected without restating the table.
    /// </summary>
    public Dictionary<string, WhatsAppCategoryRates> Countries { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = new() { Marketing = 0.0250m, Utility = 0.0040m, Authentication = 0.0135m },
        ["IN"] = new() { Marketing = 0.0099m, Utility = 0.0014m, Authentication = 0.0014m },
        ["GB"] = new() { Marketing = 0.0529m, Utility = 0.0220m, Authentication = 0.0310m },
        ["DE"] = new() { Marketing = 0.1365m, Utility = 0.0550m, Authentication = 0.0768m },
        ["FR"] = new() { Marketing = 0.1432m, Utility = 0.0300m, Authentication = 0.0609m },
        ["ES"] = new() { Marketing = 0.0615m, Utility = 0.0200m, Authentication = 0.0224m },
        ["IT"] = new() { Marketing = 0.0691m, Utility = 0.0300m, Authentication = 0.0338m },
        ["NL"] = new() { Marketing = 0.1597m, Utility = 0.0600m, Authentication = 0.0810m },
        ["CA"] = new() { Marketing = 0.0250m, Utility = 0.0040m, Authentication = 0.0135m },
        ["AU"] = new() { Marketing = 0.0529m, Utility = 0.0200m, Authentication = 0.0280m },
        ["AE"] = new() { Marketing = 0.0340m, Utility = 0.0150m, Authentication = 0.0170m },
        ["SG"] = new() { Marketing = 0.0836m, Utility = 0.0300m, Authentication = 0.0400m }
    };

    public WhatsAppCategoryRates RatesFor(string? countryCode) =>
        countryCode is not null && Countries.TryGetValue(countryCode, out var rates) ? rates : Default;
}
