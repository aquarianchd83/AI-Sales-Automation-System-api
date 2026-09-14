namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// One country this platform shows/quotes a localized price for, plus the exchange rate used to
/// convert a plan's base USD price into that country's currency. Not what Stripe actually charges -
/// Checkout still runs in USD (Plan.PriceMonthlyCents/StripePriceId) for this phase; this is a
/// display/quote figure only, computed server-side so every client (the signup country picker, the
/// tenant billing screen) reads the same numbers.
/// </summary>
public record RegionalPricing(string CountryCode, string CountryName, string CurrencyCode, string CurrencySymbol, decimal RateToUsd);

/// <summary>
/// A small, hand-maintained catalog - not a DB table - because these rates are meant to change on the
/// order of "redeploy," not "PlatformSuperAdmin edits a screen." If that stops being true (rates need
/// to move more often than a deploy allows), promote this to a DB-backed table with its own tiny CRUD
/// screen, the same shape as the Plan catalog's. Any country not listed here falls back to USD via
/// <see cref="Resolve"/> - never an error, never a missing price.
/// </summary>
public static class RegionalPricingCatalog
{
    public static readonly RegionalPricing UsdDefault = new("US", "United States", "USD", "$", 1.0m);

    public static readonly IReadOnlyList<RegionalPricing> All = new List<RegionalPricing>
    {
        UsdDefault,
        new("IN", "India", "INR", "₹", 83.0m),
        new("GB", "United Kingdom", "GBP", "£", 0.79m),
        new("DE", "Germany", "EUR", "€", 0.92m),
        new("FR", "France", "EUR", "€", 0.92m),
        new("ES", "Spain", "EUR", "€", 0.92m),
        new("IT", "Italy", "EUR", "€", 0.92m),
        new("NL", "Netherlands", "EUR", "€", 0.92m),
        new("CA", "Canada", "CAD", "$", 1.36m),
        new("AU", "Australia", "AUD", "$", 1.52m),
        new("AE", "United Arab Emirates", "AED", "د.إ", 3.67m),
        new("SG", "Singapore", "SGD", "S$", 1.34m),
    };

    private static readonly Dictionary<string, RegionalPricing> ByCountryCode =
        All.ToDictionary(r => r.CountryCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Never null - an unlisted or missing country code falls back to <see cref="UsdDefault"/>
    /// (rate 1, so the "converted" amount is just the base USD price), the same treatment as a tenant
    /// who never picked a country at signup.</summary>
    public static RegionalPricing Resolve(string? countryCode) =>
        countryCode is not null && ByCountryCode.TryGetValue(countryCode, out var pricing) ? pricing : UsdDefault;

    /// <summary>Used to validate a tenant deliberately setting/changing their own Country (self-service
    /// or a PlatformSuperAdmin override) - unlike <see cref="Resolve"/>, which silently accepts any
    /// code (even one this catalog doesn't price) and falls back to USD, a deliberate change should be
    /// rejected if it wouldn't actually produce a localized price. Same "must be one of the platform's
    /// supported ids" treatment TimeZoneCatalog.IsValidId gives Timezone.</summary>
    public static bool IsValidCode(string? countryCode) =>
        countryCode is not null && ByCountryCode.ContainsKey(countryCode);
}
