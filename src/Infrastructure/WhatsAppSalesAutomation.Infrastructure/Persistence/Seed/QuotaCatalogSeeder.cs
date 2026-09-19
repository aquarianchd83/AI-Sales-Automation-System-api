using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Starter quotas per plan and the credit-pack catalog. Insert-only and idempotent, like
/// <see cref="PlanSeeder"/>: a plan that already has any quota row, and a catalog that already has any
/// pack, is left exactly as a platform admin edited it. The numbers are placeholders sized to stay under
/// each plan's price at the configured Meta and Claude rates - review them against real margins.
/// </summary>
public static class QuotaCatalogSeeder
{
    // Included per billing period: WhatsApp (weighted units), AI conversations, lead candidates.
    private static readonly (string PlanCode, decimal WhatsApp, decimal Ai, decimal Leads)[] PlanQuotas =
    {
        ("starter", 500m, 500m, 50m),
        ("growth", 3_000m, 5_000m, 500m),
        ("scale", 15_000m, 30_000m, 3_000m)
    };

    private static readonly (QuotaType Type, string Name, decimal Units, int PriceCents)[] Packs =
    {
        (QuotaType.WhatsAppMessages, "1,000 WhatsApp messages", 1_000m, 2_000),
        (QuotaType.WhatsAppMessages, "5,000 WhatsApp messages", 5_000m, 8_500),
        (QuotaType.AiConversations, "1,000 AI conversations", 1_000m, 800),
        (QuotaType.AiConversations, "5,000 AI conversations", 5_000m, 3_500),
        (QuotaType.LeadCandidates, "100 lead candidates", 100m, 600),
        (QuotaType.LeadCandidates, "500 lead candidates", 500m, 2_500)
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var plansWithQuotas = await db.PlanQuotas.Select(q => q.PlanId).Distinct().ToListAsync();
        var plans = await db.Plans.ToListAsync();

        foreach (var spec in PlanQuotas)
        {
            var plan = plans.FirstOrDefault(p => string.Equals(p.Code, spec.PlanCode, StringComparison.OrdinalIgnoreCase));
            if (plan is null || plansWithQuotas.Contains(plan.Id))
                continue;

            db.PlanQuotas.Add(new PlanQuota { PlanId = plan.Id, QuotaType = QuotaType.WhatsAppMessages, IncludedUnits = spec.WhatsApp });
            db.PlanQuotas.Add(new PlanQuota { PlanId = plan.Id, QuotaType = QuotaType.AiConversations, IncludedUnits = spec.Ai });
            db.PlanQuotas.Add(new PlanQuota { PlanId = plan.Id, QuotaType = QuotaType.LeadCandidates, IncludedUnits = spec.Leads });
        }

        if (!await db.CreditPacks.AnyAsync())
        {
            foreach (var pack in Packs)
                db.CreditPacks.Add(new CreditPack { QuotaType = pack.Type, Name = pack.Name, Units = pack.Units, PriceCents = pack.PriceCents });
        }

        await db.SaveChangesAsync();
    }
}
