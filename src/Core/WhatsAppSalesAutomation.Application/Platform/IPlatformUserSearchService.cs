namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Platform Admin Console's cross-tenant user search (spec item #6) - "which org is this email in,"
/// for support lookups. Not a CRUD screen: no create/update/role-assignment here, that stays on each
/// tenant's own Users & Roles screen (<c>UserService</c>/<c>UsersController</c>).
/// </summary>
public interface IPlatformUserSearchService
{
    /// <summary>Matches on email or full name, case-insensitive substring. Returns empty for a blank
    /// <paramref name="search"/> rather than every user on the platform - this endpoint is a targeted
    /// lookup, not a way to enumerate every tenant's users at once.</summary>
    /// <remarks>When <paramref name="tenantId"/> is set the lookup is scoped to that one tenant, so a blank
    /// <paramref name="search"/> lists that tenant's users (the tenant list's user-count link).</remarks>
    Task<IReadOnlyList<PlatformUserSearchResultDto>> SearchAsync(string? search, Guid? tenantId = null, CancellationToken cancellationToken = default);
}
