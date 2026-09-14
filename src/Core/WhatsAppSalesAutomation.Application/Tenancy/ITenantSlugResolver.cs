namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>
/// Resolves the URL-safe slug for a new tenant - shared by every place a new Tenant row gets
/// created (self-serve <c>AuthService.SignUpAsync</c> and the Platform Admin Console's
/// <c>PlatformTenantService.CreateAsync</c>) so the derivation/de-duplication rule can't drift
/// between the two call sites.
/// </summary>
public interface ITenantSlugResolver
{
    /// <summary>A caller-supplied slug is lowercased and used as-is, or rejected with
    /// <see cref="Application.Common.Exceptions.ConflictException"/> if it collides with an
    /// existing tenant - it is never silently altered. With no slug supplied, one is derived from
    /// <paramref name="companyName"/> and de-duplicated by appending "-2", "-3", etc.</summary>
    Task<string> ResolveAsync(string? requestedSlug, string companyName, CancellationToken cancellationToken = default);
}
