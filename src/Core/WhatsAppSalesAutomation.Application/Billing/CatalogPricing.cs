using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>Explicit per-country amounts for one plan or pack, keyed by country code (case-insensitive).</summary>
public class CountryPrices : Dictionary<string, decimal>
{
    public CountryPrices() : base(StringComparer.OrdinalIgnoreCase) { }
}

/// <summary>
/// Resolves what one tenant pays for a catalog item. An explicit price set for the tenant's country wins and is
/// charged exactly; otherwise the item's base USD price is converted at the regional catalog rate, as before. The
/// USD figure recorded on a payment is always derived from what was actually charged, so a per-country price and
/// the platform's USD reporting stay consistent.
/// </summary>
public static class CatalogPricing
{
    public static decimal Local(int baseCents, RegionalPricing pricing, CountryPrices? explicitPrices) =>
        explicitPrices is not null && explicitPrices.TryGetValue(pricing.CountryCode, out var amount)
            ? amount
            : Math.Round(baseCents / 100m * pricing.RateToUsd, 2, MidpointRounding.AwayFromZero);

    /// <summary>The USD-cents equivalent of a local charge. For a converted price this round-trips to the base price.</summary>
    public static int ToUsdCents(decimal local, RegionalPricing pricing) =>
        (int)Math.Round(local / pricing.RateToUsd * 100m, MidpointRounding.AwayFromZero);

    /// <summary>The (USD cents, local amount) pair a payment records.</summary>
    public static (int UsdCents, decimal Local) Charge(int baseCents, RegionalPricing pricing, CountryPrices? explicitPrices)
    {
        if (explicitPrices is not null && explicitPrices.TryGetValue(pricing.CountryCode, out var amount))
            return (ToUsdCents(amount, pricing), amount);

        return (baseCents, Local(baseCents, pricing, null));
    }

    public static async Task<Dictionary<Guid, CountryPrices>> LoadPlanPricesAsync(
        IApplicationDbContext context, IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken)
    {
        var rows = await context.PlanPrices.Where(p => planIds.Contains(p.PlanId)).ToListAsync(cancellationToken);
        return Group(rows.Select(r => (r.PlanId, r.CountryCode, r.Amount)));
    }

    public static async Task<Dictionary<Guid, CountryPrices>> LoadPackPricesAsync(
        IApplicationDbContext context, IReadOnlyCollection<Guid> packIds, CancellationToken cancellationToken)
    {
        var rows = await context.CreditPackPrices.Where(p => packIds.Contains(p.CreditPackId)).ToListAsync(cancellationToken);
        return Group(rows.Select(r => (r.CreditPackId, r.CountryCode, r.Amount)));
    }

    private static Dictionary<Guid, CountryPrices> Group(IEnumerable<(Guid Id, string Country, decimal Amount)> rows)
    {
        var result = new Dictionary<Guid, CountryPrices>();
        foreach (var (id, country, amount) in rows)
        {
            if (!result.TryGetValue(id, out var prices))
                result[id] = prices = new CountryPrices();
            prices[country] = amount;
        }

        return result;
    }
}
