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
    Task<IReadOnlyList<PlatformUserSearchResultDto>> SearchAsync(string? search, CancellationToken cancellationToken = default);
}
