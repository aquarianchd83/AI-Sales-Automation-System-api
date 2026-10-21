using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// The platform's two plans, and every tenant on one of them. Run at every start-up and safe to repeat:
/// <list type="bullet">
/// <item><description><b>Trial</b> - the minimum set of features and a one-time starter balance. Free, and never expires.</description></item>
/// <item><description><b>Premium</b> - every feature, and credits added to the wallet each month. Never expires either.</description></item>
/// </list>
/// Insert-only for what an admin may have edited: a plan that exists keeps its name, credits and prices, and a plan that already has features keeps them.
/// Plans from before the single-wallet model (starter, growth, scale) are retired, not deleted, so old payments still point at something; the tenants on
/// them are moved onto Trial, or Premium if they were paying. Every tenant is then put on its plan, which grants the credits (each grant happens once).
/// </summary>
public static class PlanSeeder
{
    /// <summary>What Premium is sold at, in USD, until the admin sets a price per country on the plan editor.</summary>
    private const decimal PremiumPriceUsd = 29m;

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var trial = await EnsurePlanAsync(db, PlatformFeatureCatalog.TrialPlanCode, "Trial", 0, PlatformFeatureCatalog.TrialStarterCredits, 0m, 1, 100, 1, 20, 25);
        var premium = await EnsurePlanAsync(db, PlatformFeatureCatalog.PremiumPlanCode, "Premium", (int)(PremiumPriceUsd * 100), 0m, PlatformFeatureCatalog.PremiumMonthlyCredits, 10, 10_000, 25, 500, 100);

        await EnsureFeaturesAsync(db, trial, PlatformFeatureCatalog.TrialFeatures);
        await EnsureFeaturesAsync(db, premium, PlatformFeatureCatalog.PremiumFeatures);
        await EnsurePremiumPricesAsync(db, premium);

        await RetireLegacyPlansAsync(db, trial, premium);
        await PutEveryTenantOnItsPlanAsync(db, services.GetRequiredService<ICreditAllocationService>(), trial);
    }

    private static async Task<Plan> EnsurePlanAsync(
        ApplicationDbContext db, string code, string name, int priceCents, decimal starterCredits, decimal monthlyCredits,
        int maxUsers, int maxMessagesPerMonth, int maxCampaigns, int maxKnowledgeBaseArticles, int maxLeadDiscoveryBatchSize)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == code);
        if (plan is not null)
            return plan;

        plan = new Plan
        {
            Code = code,
            Name = name,
            MaxUsers = maxUsers,
            MaxMessagesPerMonth = maxMessagesPerMonth,
            MaxCampaigns = maxCampaigns,
            MaxKnowledgeBaseArticles = maxKnowledgeBaseArticles,
            MaxLeadDiscoveryBatchSize = maxLeadDiscoveryBatchSize,
            PriceMonthlyCents = priceCents,
            StarterCredits = starterCredits,
            MonthlyCredits = monthlyCredits,
            IsActive = true
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    }

    private static async Task EnsureFeaturesAsync(ApplicationDbContext db, Plan plan, IReadOnlyList<PlatformFeature> features)
    {
        // A plan that already has features was set up by someone: leave it.
        if (await db.PlanFeatures.AnyAsync(f => f.PlanId == plan.Id))
            return;

        foreach (var feature in features)
            db.PlanFeatures.Add(new PlanFeature { PlanId = plan.Id, Feature = feature });
        await db.SaveChangesAsync();
    }

    /// <summary>A plan with no price in a country is not sold there, so Premium starts with a price in every country the platform prices for.</summary>
    private static async Task EnsurePremiumPricesAsync(ApplicationDbContext db, Plan premium)
    {
        if (await db.PlanPrices.AnyAsync(p => p.PlanId == premium.Id))
            return;

        foreach (var region in RegionalPricingCatalog.All)
            db.PlanPrices.Add(new PlanPrice { PlanId = premium.Id, CountryCode = region.CountryCode, Amount = Math.Round(PremiumPriceUsd * region.RateToUsd, 0, MidpointRounding.AwayFromZero) });
        await db.SaveChangesAsync();
    }

    private static async Task RetireLegacyPlansAsync(ApplicationDbContext db, Plan trial, Plan premium)
    {
        var keep = new[] { trial.Id, premium.Id };

        foreach (var legacy in await db.Plans.Where(p => !keep.Contains(p.Id) && p.IsActive).ToListAsync())
            legacy.IsActive = false;

        var stranded = await db.Subscriptions.IgnoreQueryFilters().Where(s => s.PlanId != null && !keep.Contains(s.PlanId.Value)).ToListAsync();
        foreach (var subscription in stranded)
            subscription.PlanId = subscription.Status == SubscriptionStatus.Active ? premium.Id : trial.Id;

        await db.SaveChangesAsync();
    }

    private static async Task PutEveryTenantOnItsPlanAsync(ApplicationDbContext db, ICreditAllocationService credits, Plan trial)
    {
        var plans = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(s => s.TenantId, s => s.PlanId);
        var tenantIds = await db.Tenants.IgnoreQueryFilters().AsNoTracking().Select(t => t.Id).ToListAsync();

        foreach (var tenantId in tenantIds)
            await credits.AssignPlanAsync(tenantId, plans.GetValueOrDefault(tenantId) ?? trial.Id);
    }
}
