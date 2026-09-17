using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotently inserts the platform's starter plan catalog by <see cref="Plan.Code"/> - run once at
/// startup, same "safe to run every restart" shape as IdentitySeeder's role seeding. Unlike
/// IdentitySeeder this always runs, not gated behind a Seed:* config flag: the plan catalog is not
/// dummy/dev-only data, every environment (including production) needs these rows to exist for
/// signup/billing to work at all on a fresh database.
///
/// Insert-only, deliberately: a plan is now a PlatformSuperAdmin-owned resource, created/edited/
/// retired through the Platform Admin Console's Plan catalog screen (see
/// IPlatformBillingService.CreatePlanAsync/UpdatePlanAsync). This seeder used to also re-sync every
/// existing row's Name/limits/price from the catalog below on every boot - that would have silently
/// reverted any edit made through that screen on the next deploy/restart, so it now only fills in a
/// Code that doesn't exist yet and never touches an existing row again.
/// </summary>
public static class PlanSeeder
{
    private static readonly (string Code, string Name, int MaxUsers, int MaxMessagesPerMonth, int MaxCampaigns, int MaxKnowledgeBaseArticles, int PriceMonthlyCents, int MaxLeadDiscoveryBatchSize)[] Catalog =
    {
        ("starter", "Starter", 3, 1_000, 5, 20, 2900, 25),
        ("growth", "Growth", 10, 10_000, 25, 100, 9900, 100),
        ("scale", "Scale", 50, 100_000, 100, 500, 29900, 250)
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var existingCodes = await db.Plans.Select(p => p.Code).ToListAsync();
        var existingCodeSet = new HashSet<string>(existingCodes, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var spec in Catalog)
        {
            if (existingCodeSet.Contains(spec.Code))
                continue;

            db.Plans.Add(new Plan
            {
                Code = spec.Code,
                Name = spec.Name,
                MaxUsers = spec.MaxUsers,
                MaxMessagesPerMonth = spec.MaxMessagesPerMonth,
                MaxCampaigns = spec.MaxCampaigns,
                MaxKnowledgeBaseArticles = spec.MaxKnowledgeBaseArticles,
                MaxLeadDiscoveryBatchSize = spec.MaxLeadDiscoveryBatchSize,
                PriceMonthlyCents = spec.PriceMonthlyCents,
                IsActive = true
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }
}
