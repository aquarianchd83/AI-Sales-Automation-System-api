using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Tenancy;

/// <summary>
/// One customer business account on the platform - the root everything else in the system hangs off
/// via <see cref="ITenantOwned.TenantId"/>. Not itself <see cref="ITenantOwned"/> - it is the tenant,
/// not something owned by one, and carries no query filter.
/// </summary>
public class Tenant : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase, URL-safe, unique. Doubles as the subdomain slug (e.g. "acme" for
    /// acme.saleautomation.com) once the frontend's wildcard DNS routing is wired up - see
    /// <c>TenantsController.GetBySlugAsync</c>.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public TenantStatus Status { get; set; } = TenantStatus.Trial;

    /// <summary>The ApplicationUser who signed this tenant up - set once, at the end of
    /// AuthService.SignUpAsync's transaction, after the first admin user exists.</summary>
    public Guid? OwnerUserId { get; set; }

    public DateTime? TrialEndsAtUtc { get; set; }

    /// <summary>Wired up in the billing phase - null means "no active plan selected yet" (still on trial
    /// or between plans), not an error.</summary>
    public Guid? PlanId { get; set; }

    /// <summary>ISO 3166-1 alpha-2 (e.g. "US", "IN"), picked on the signup form - null for a tenant
    /// who signed up before this existed, or who left it blank. Drives which currency/exchange rate
    /// IBillingService.GetPlansAsync quotes plan prices in (see RegionalPricingCatalog); null or any
    /// unmatched code falls back to USD, never an error.</summary>
    public string? CountryCode { get; set; }
}
