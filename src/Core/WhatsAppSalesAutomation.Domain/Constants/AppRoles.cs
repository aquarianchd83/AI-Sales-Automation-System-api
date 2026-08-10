namespace WhatsAppSalesAutomation.Domain.Constants;

/// <summary>
/// The four roles supported by the platform. Kept as compile-time constants so they can be
/// used directly in <c>[Authorize(Roles = ...)]</c> attributes, which require constant expressions.
/// </summary>
public static class AppRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string SalesManager = "SalesManager";
    public const string SalesAgent = "SalesAgent";

    public static readonly IReadOnlyList<string> All = new[] { SuperAdmin, Admin, SalesManager, SalesAgent };
}
