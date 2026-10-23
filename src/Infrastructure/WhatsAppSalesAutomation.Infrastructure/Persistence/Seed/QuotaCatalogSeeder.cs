using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// The credit packs a tenant can buy to top up its single wallet. Insert-only and idempotent, like <see cref="PlanSeeder"/>: once a credit pack
/// exists it is left as a platform admin edited it. Packs from before the single wallet (per-feature units) are retired, not deleted, so old payments
/// still point at them. The prices are placeholders - review them against real margins.
/// </summary>
public static class QuotaCatalogSeeder
{
    private static readonly (string Name, decimal Credits, decimal PriceUsd)[] Packs =
    {
        ("100 credits", 100m, 3m),
        ("500 credits", 500m, 12m),
        ("2,000 credits", 2_000m, 40m)
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        foreach (var legacy in await db.CreditPacks.Where(p => p.QuotaType != QuotaType.Credits && p.IsActive).ToListAsync())
            legacy.IsActive = false;

        if (!await db.CreditPacks.AnyAsync(p => p.QuotaType == QuotaType.Credits))
        {
            foreach (var (name, credits, priceUsd) in Packs)
            {
                var pack = new CreditPack { QuotaType = QuotaType.Credits, Name = name, Units = credits, PriceCents = (int)(priceUsd * 100) };
                db.CreditPacks.Add(pack);

                // A pack with no price in a country is not sold there, so each starts with one in every country the platform prices for.
                foreach (var region in RegionalPricingCatalog.All)
                    db.CreditPackPrices.Add(new CreditPackPrice { CreditPackId = pack.Id, CountryCode = region.CountryCode, Amount = Math.Round(priceUsd * region.RateToUsd, 0, MidpointRounding.AwayFromZero) });
            }
        }

        await db.SaveChangesAsync();
    }
}
