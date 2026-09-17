using Microsoft.AspNetCore.Identity;

namespace WhatsAppSalesAutomation.Domain.Entities.Identity;

/// <summary>
/// Platform user, backed by ASP.NET Core Identity. Roles (SuperAdmin/Admin/SalesManager/SalesAgent)
/// are managed through Identity's UserRoles join table rather than a custom one.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// The tenant this user belongs to. Null only for a <c>PlatformSuperAdmin</c> account (the SaaS
    /// operator, not a tenant's own admin) - every other user has exactly one tenant, set once at
    /// creation (signup or <c>UserService.CreateAsync</c>) and never reassigned. Deliberately not
    /// <see cref="Common.ITenantOwned"/>: that interface's non-nullable <c>TenantId</c> can't represent
    /// "no tenant", and Users are reached through <c>UserManager&lt;ApplicationUser&gt;</c>, not
    /// <c>IApplicationDbContext</c>, so <c>ApplicationDbContext.OnModelCreating</c> wires this entity's
    /// filter as a special case instead of picking it up through the reflective
    /// <c>WhatsAppSalesAutomation.Domain.Common.ITenantOwned</c> pass.
    /// </summary>
    public Guid? TenantId { get; set; }

    public string FullName { get; set; } = string.Empty;

    /// <summary>Soft "disabled" flag. Deactivated users can no longer authenticate.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The user's own display timezone (an IANA id from TimeZoneCatalog), set on their profile page. Null
    /// means "never chosen" and reads as the platform default.
    ///
    /// Deliberately separate from <c>Tenant.Timezone</c>, which is operational - it decides when a tenant's
    /// scheduled campaigns actually send. This one is presentation only: which timezone timestamps are
    /// rendered in for this person. A PlatformSuperAdmin has no tenant at all, so without this there was
    /// nothing to render their console's timestamps against.
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>The user's own country (a RegionalPricingCatalog code), set on their profile page.
    /// Informational: unlike <c>Tenant.CountryCode</c> it drives no pricing or currency.</summary>
    public string? CountryCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
