namespace WhatsAppSalesAutomation.Infrastructure.Identity;

/// <summary>Custom (non-<see cref="System.Security.Claims.ClaimTypes"/>) claim type names issued by
/// <see cref="JwtTokenService"/> and read back by <see cref="CurrentUserService"/> - kept as one shared
/// constant so the issuing and reading sides can never drift apart on the literal string.</summary>
public static class JwtClaimNames
{
    public const string TenantId = "tenant_id";

    /// <summary>Present only on an impersonation access token (see
    /// <see cref="JwtTokenService.GenerateImpersonationAccessToken"/>) - the acting PlatformSuperAdmin's
    /// user id, so every request made during a support session is traceable back to who initiated it.</summary>
    public const string ImpersonatedBy = "impersonated_by";
}
