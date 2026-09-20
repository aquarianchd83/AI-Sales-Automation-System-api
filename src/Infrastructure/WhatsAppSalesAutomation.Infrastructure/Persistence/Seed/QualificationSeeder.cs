using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Gives every existing tenant the default qualification schema and scoring rules, and moves whatever
/// each lead already has in <see cref="Lead.Budget"/>/<see cref="Lead.Interest"/>/
/// <see cref="Lead.PurchaseTimeline"/> into <see cref="LeadQualificationValue"/> rows.
///
/// The backfill is the part that matters. Without it, a tenant upgrading to configurable qualification
/// would have an agent that re-asks every customer for a budget it was already told - the data is
/// sitting right there on the Lead, just not where the planner looks. Values are backfilled at
/// confidence 1.0 because they were already accepted under the old shape; anything lower would make
/// the agent treat them as shaky and ask again, which is exactly the outcome this prevents.
///
/// Runs at startup, always, insert-only - the same shape as <see cref="PlanSeeder"/> and
/// <see cref="FaqSeeder"/>. Safe to re-run: seeding skips keys a tenant already has, and the backfill
/// skips leads that already have a current value for the key. A tenant that has since customised its
/// own schema is never touched again.
///
/// Deliberately NOT a data migration: a migration runs once per database and cannot be re-run after a
/// partial failure, while this converges on every boot until it has nothing left to do.
/// </summary>
public static class QualificationSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync();
        if (tenantIds.Count == 0)
            return;

        await SeedFieldsAsync(db, tenantIds);
        await SeedScoringRulesAsync(db, tenantIds);
        await BackfillLeadValuesAsync(db);
    }

    private static async Task SeedFieldsAsync(ApplicationDbContext db, IReadOnlyList<Guid> tenantIds)
    {
        // IgnoreQueryFilters throughout: this runs at startup with no ambient tenant, so the usual
        // per-tenant filter would match nothing at all.
        var existing = await db.QualificationFields
            .IgnoreQueryFilters()
            .Where(f => !f.IsDeleted)
            .Select(f => new { f.TenantId, f.FieldKey })
            .ToListAsync();

        var have = existing
            .Select(x => (x.TenantId, x.FieldKey.ToLowerInvariant()))
            .ToHashSet();

        var changed = false;

        foreach (var tenantId in tenantIds)
        {
            foreach (var seed in QualificationDefaults.Fields)
            {
                if (have.Contains((tenantId, seed.FieldKey)))
                    continue;

                db.QualificationFields.Add(new QualificationField
                {
                    TenantId = tenantId,
                    FieldKey = seed.FieldKey,
                    DisplayName = seed.DisplayName,
                    Description = seed.Description,
                    Question = seed.Question,
                    DataType = seed.DataType,
                    IsRequired = seed.IsRequired,
                    Priority = seed.Priority,
                    ScoreWeight = seed.ScoreWeight,
                    IsActive = true,
                    SortOrder = seed.SortOrder
                });
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task SeedScoringRulesAsync(ApplicationDbContext db, IReadOnlyList<Guid> tenantIds)
    {
        var existing = await db.LeadScoringRules
            .IgnoreQueryFilters()
            .Select(r => new { r.TenantId, r.RuleKey })
            .ToListAsync();

        var have = existing
            .Select(x => (x.TenantId, x.RuleKey.ToLowerInvariant()))
            .ToHashSet();

        var changed = false;

        foreach (var tenantId in tenantIds)
        {
            foreach (var seed in QualificationDefaults.ScoringRules)
            {
                if (have.Contains((tenantId, seed.RuleKey)))
                    continue;

                db.LeadScoringRules.Add(new LeadScoringRule
                {
                    TenantId = tenantId,
                    RuleKey = seed.RuleKey,
                    DisplayName = seed.DisplayName,
                    RuleType = seed.RuleType,
                    MatchValue = seed.MatchValue,
                    Points = seed.Points,
                    OncePerLead = seed.OncePerLead,
                    MarksLeadHot = seed.MarksLeadHot,
                    IsActive = true,
                    SortOrder = seed.SortOrder
                });
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task BackfillLeadValuesAsync(ApplicationDbContext db)
    {
        // Only leads that actually have something to move. On a re-run this shrinks to nothing, since
        // every such lead already has a current value for those keys.
        var leads = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.Budget != null || l.Interest != null || l.PurchaseTimeline != null)
            .Select(l => new { l.Id, l.TenantId, l.Budget, l.Interest, l.PurchaseTimeline })
            .ToListAsync();

        if (leads.Count == 0)
            return;

        var leadIds = leads.Select(l => l.Id).ToList();

        var alreadyCaptured = (await db.LeadQualificationValues
                .IgnoreQueryFilters()
                .Where(v => leadIds.Contains(v.LeadId) && !v.IsSuperseded)
                .Select(v => new { v.LeadId, v.FieldKey })
                .ToListAsync())
            .Select(x => (x.LeadId, x.FieldKey.ToLowerInvariant()))
            .ToHashSet();

        var fields = await db.QualificationFields
            .IgnoreQueryFilters()
            .Where(f => !f.IsDeleted)
            .Select(f => new { f.Id, f.TenantId, f.FieldKey })
            .ToListAsync();

        var fieldIdByTenantKey = fields
            .ToDictionary(f => (f.TenantId, f.FieldKey.ToLowerInvariant()), f => f.Id);

        var changed = false;

        foreach (var lead in leads)
        {
            changed |= TryAdd(lead.Id, lead.TenantId, QualificationDefaults.BudgetKey, lead.Budget);
            changed |= TryAdd(lead.Id, lead.TenantId, QualificationDefaults.InterestKey, lead.Interest);
            changed |= TryAdd(lead.Id, lead.TenantId, QualificationDefaults.PurchaseTimelineKey, lead.PurchaseTimeline);
        }

        if (changed)
            await db.SaveChangesAsync();

        bool TryAdd(Guid leadId, Guid tenantId, string fieldKey, string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            if (alreadyCaptured.Contains((leadId, fieldKey)))
                return false;

            // A tenant that removed one of the default fields before this ran simply has no row to
            // attach the value to - skipping is correct, not an error.
            if (!fieldIdByTenantKey.TryGetValue((tenantId, fieldKey), out var fieldId))
                return false;

            db.LeadQualificationValues.Add(new LeadQualificationValue
            {
                TenantId = tenantId,
                LeadId = leadId,
                FieldId = fieldId,
                FieldKey = fieldKey,
                RawValue = rawValue.Trim(),
                // Left null rather than normalized: normalizing here would reinterpret values captured
                // under the old free-text shape, and the raw value is what the agent needs to not
                // re-ask. Normalization happens naturally the next time the customer restates it.
                NormalizedValue = null,
                ExtractionConfidence = 1.0
            });

            return true;
        }
    }
}
