namespace WhatsAppSalesAutomation.Domain.Constants;

/// <summary>
/// The roles supported by the platform. Kept as compile-time constants so they can be used directly
/// in <c>[Authorize(Roles = ...)]</c> attributes, which require constant expressions.
/// </summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string SalesManager = "SalesManager";
    public const string SalesAgent = "SalesAgent";

    /// <summary>
    /// The SaaS operator role - runs the platform itself, not any one tenant's account. Users with this
    /// role have <c>ApplicationUser.TenantId == null</c> and are excluded from <see cref="All"/>, which
    /// stays the three tenant-scoped roles a tenant Admin can assign to their own users (see
    /// <c>UserService.GetAllRolesAsync</c>/<c>AssignRolesAsync</c>).
    /// </summary>
    public const string PlatformSuperAdmin = "PlatformSuperAdmin";

    // A "SuperAdmin" role used to sit above Admin here, a leftover from before multi-tenancy split
    // top-level access into PlatformSuperAdmin (operates the SaaS itself) and Admin (runs one tenant).
    // Every [Authorize] check already treated the two as equivalent (always "SuperAdmin,Admin" together),
    // so it carried no real distinction - retired in favor of just Admin.
    public static readonly IReadOnlyList<string> All = new[] { Admin, SalesManager, SalesAgent };
}
