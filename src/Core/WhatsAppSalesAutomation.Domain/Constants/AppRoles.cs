namespace WhatsAppSalesAutomation.Domain.Constants;

/// <summary>
/// The roles supported by the platform. Kept as compile-time constants so they can be used directly
/// in <c>[Authorize(Roles = ...)]</c> attributes, which require constant expressions.
/// </summary>
public static class AppRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string SalesManager = "SalesManager";
    public const string SalesAgent = "SalesAgent";

    /// <summary>
    /// The SaaS operator role - runs the platform itself, not any one tenant's account. Users with this
    /// role have <c>ApplicationUser.TenantId == null</c> and are excluded from <see cref="All"/>, which
    /// stays the four tenant-scoped roles a tenant Admin/SuperAdmin can assign to their own users (see
    /// <c>UserService.GetAllRolesAsync</c>/<c>AssignRolesAsync</c>).
    /// </summary>
    public const string PlatformSuperAdmin = "PlatformSuperAdmin";

    public static readonly IReadOnlyList<string> All = new[] { SuperAdmin, Admin, SalesManager, SalesAgent };
}
