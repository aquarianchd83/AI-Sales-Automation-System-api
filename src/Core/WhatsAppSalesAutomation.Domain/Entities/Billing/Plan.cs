using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One tier of the platform-wide plan catalog - global, not <see cref="ITenantOwned"/>: every tenant
/// picks from the same small set of plans, the plans themselves don't belong to any one tenant. Seeded
/// idempotently by <c>PlanSeeder</c> at startup, keyed by <see cref="Code"/> (not <see cref="Id"/> - a
/// stable string survives being re-seeded across environments/restores in a way a fresh Guid would
/// not). Edited by hand in the DB or through a future admin screen, never through tenant-facing API.
/// </summary>
public class Plan : BaseEntity
{
    /// <summary>Stable idempotency key for PlanSeeder's upsert, e.g. "starter", "growth" - never
    /// shown to a tenant, never changes once a plan exists (rename via <see cref="Name"/> instead).</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Stripe's Price id for this plan's recurring subscription - what
    /// StripeBillingService.CreateCheckoutSessionAsync actually sells. Null only for a plan that
    /// exists locally but hasn't been created on Stripe yet (e.g. mid-setup) - IBillingService.CreateCheckoutSessionAsync
    /// refuses to sell a plan with no price id rather than letting Stripe reject the session.</summary>
    public string? StripePriceId { get; set; }

    public int MaxUsers { get; set; }

    public int MaxMessagesPerMonth { get; set; }

    public int MaxCampaigns { get; set; }

    public int MaxKnowledgeBaseArticles { get; set; }

    public int PriceMonthlyCents { get; set; }

    /// <summary>False retires a plan from new signups/upgrades without deleting it - existing
    /// Subscriptions referencing it keep working (PlanLimitsService still reads its limits), same
    /// "soft retire, don't break history" reasoning as MessageTemplate.IsActive.</summary>
    public bool IsActive { get; set; } = true;
}
