using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotently upserts the platform's plan catalog by <see cref="Plan.Code"/> - run once at startup,
/// same "safe to run every restart" shape as IdentitySeeder's role seeding. Unlike IdentitySeeder this
/// always runs, not gated behind a Seed:* config flag: the plan catalog is not dummy/dev-only data,
/// every environment (including production) needs these rows to exist for signup/billing to work at
/// all. StripePriceId is deliberately left null here - wiring a plan to a real Stripe Price is an
/// operational step (create it in the Stripe dashboard, paste the id back), not something to invent a
/// placeholder value for.
/// </summary>
public static class PlanSeeder
{
    private static readonly (string Code, string Name, int MaxUsers, int MaxMessagesPerMonth, int MaxCampaigns, int MaxKnowledgeBaseArticles, int PriceMonthlyCents)[] Catalog =
    {
        ("starter", "Starter", 3, 1_000, 5, 20, 2900),
        ("growth", "Growth", 10, 10_000, 25, 100, 9900),
        ("scale", "Scale", 50, 100_000, 100, 500, 29900)
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var existingByCode = await db.Plans.ToDictionaryAsync(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var spec in Catalog)
        {
            if (existingByCode.TryGetValue(spec.Code, out var plan))
            {
                // Re-syncs limits/price from the catalog above on every startup - a plan's numbers are
                // meant to be edited here in source control, not by hand in the DB (StripePriceId is
                // the one exception: never overwritten once set, since that's the one field this
                // seeder has no source-of-truth value for - see the class doc comment).
                plan.Name = spec.Name;
                plan.MaxUsers = spec.MaxUsers;
                plan.MaxMessagesPerMonth = spec.MaxMessagesPerMonth;
                plan.MaxCampaigns = spec.MaxCampaigns;
                plan.MaxKnowledgeBaseArticles = spec.MaxKnowledgeBaseArticles;
                plan.PriceMonthlyCents = spec.PriceMonthlyCents;
            }
            else
            {
                db.Plans.Add(new Plan
                {
                    Code = spec.Code,
                    Name = spec.Name,
                    MaxUsers = spec.MaxUsers,
                    MaxMessagesPerMonth = spec.MaxMessagesPerMonth,
                    MaxCampaigns = spec.MaxCampaigns,
                    MaxKnowledgeBaseArticles = spec.MaxKnowledgeBaseArticles,
                    PriceMonthlyCents = spec.PriceMonthlyCents,
                    IsActive = true
                });
            }

            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }
}
