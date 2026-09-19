using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>One tax charged on top of a price: "GST 18%", or "CGST 9%" and "SGST 9%" as two lines.</summary>
public record TaxLineDto(string Name, decimal RatePercent, decimal Amount);

/// <summary>What one tenant would pay for one plan or pack today: the price set for their country, the tax added on top,
/// and the exchange-rate snapshot the platform's INR reports use. All money is in the tenant's currency except
/// <see cref="TotalInr"/>.</summary>
public record PriceQuote(
    string CountryCode,
    string? StateCode,
    string CurrencyCode,
    string CurrencySymbol,
    decimal Subtotal,
    IReadOnlyList<TaxLineDto> TaxLines,
    decimal Tax,
    decimal Total,
    /// <summary>INR per one unit of the tenant's currency.</summary>
    decimal FxRateToInr,
    decimal TotalInr,
    /// <summary>The subtotal in USD cents - kept only because refund eligibility and older rows are expressed that way.</summary>
    int SubtotalUsdCents)
{
    public string TaxLinesJson => JsonSerializer.Serialize(TaxLines);
}

/// <summary>
/// The one place a price is worked out. A price comes from the country's own price row and nowhere else - a country
/// with no row gets no quote, so the plan simply isn't sold there (never a converted USD price). Tax is added on top, and
/// where the tax splits by state (India's GST) the tenant's state against the platform's decides between CGST + SGST and
/// IGST.
/// </summary>
public interface IPricingService
{
    /// <summary>Null when <paramref name="subtotal"/> is null or not positive - nothing is priced for this country.</summary>
    PriceQuote? Quote(decimal? subtotal, string? countryCode, string? stateCode);

    /// <summary>Tax lines for a subtotal - also used to size a refund's share of the tax.</summary>
    IReadOnlyList<TaxLineDto> TaxOn(decimal subtotal, string countryCode, string? stateCode);
}

public class PricingService : IPricingService
{
    private readonly TaxOptions _tax;
    private readonly FxOptions _fx;

    public PricingService(IOptionsSnapshot<TaxOptions> tax, IOptionsSnapshot<FxOptions> fx)
    {
        _tax = tax.Value;
        _fx = fx.Value;
    }

    public PriceQuote? Quote(decimal? subtotal, string? countryCode, string? stateCode)
    {
        if (subtotal is not { } amount || amount <= 0)
            return null;

        var region = RegionalPricingCatalog.Resolve(countryCode);
        var state = string.IsNullOrWhiteSpace(stateCode) ? null : stateCode.Trim().ToUpperInvariant();

        var lines = TaxOn(amount, region.CountryCode, state);
        var tax = lines.Sum(l => l.Amount);
        var total = amount + tax;

        // INR per unit of local currency: INR per USD, divided by local units per USD.
        var fxToInr = Math.Round(_fx.InrPerUsd / region.RateToUsd, 6, MidpointRounding.AwayFromZero);

        return new PriceQuote(
            region.CountryCode, state, region.CurrencyCode, region.CurrencySymbol,
            amount, lines, tax, total, fxToInr,
            Math.Round(total * fxToInr, 2, MidpointRounding.AwayFromZero),
            (int)Math.Round(amount / region.RateToUsd * 100m, MidpointRounding.AwayFromZero));
    }

    public IReadOnlyList<TaxLineDto> TaxOn(decimal subtotal, string countryCode, string? stateCode)
    {
        if (!_tax.Countries.TryGetValue(countryCode, out var rule) || rule.RatePercent <= 0)
            return Array.Empty<TaxLineDto>();

        if (!rule.SplitByState)
            return new[] { new TaxLineDto(rule.Name, rule.RatePercent, Round(subtotal * rule.RatePercent / 100m)) };

        // Same state as the platform: the tax is shared between centre and state. Anywhere else - or a state we don't
        // know - it is one integrated tax.
        var sameState = !string.IsNullOrWhiteSpace(stateCode)
                        && !string.IsNullOrWhiteSpace(_tax.SupplierStateCode)
                        && string.Equals(stateCode.Trim(), _tax.SupplierStateCode.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!sameState)
            return new[] { new TaxLineDto("IGST", rule.RatePercent, Round(subtotal * rule.RatePercent / 100m)) };

        var half = rule.RatePercent / 2m;
        return new[]
        {
            new TaxLineDto("CGST", half, Round(subtotal * half / 100m)),
            new TaxLineDto("SGST", half, Round(subtotal * half / 100m))
        };
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
