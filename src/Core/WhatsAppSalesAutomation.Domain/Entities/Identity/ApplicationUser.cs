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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
