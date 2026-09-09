namespace WhatsAppSalesAutomation.Infrastructure.Identity;

/// <summary>Custom (non-<see cref="System.Security.Claims.ClaimTypes"/>) claim type names issued by
/// <see cref="JwtTokenService"/> and read back by <see cref="CurrentUserService"/> - kept as one shared
/// constant so the issuing and reading sides can never drift apart on the literal string.</summary>
public static class JwtClaimNames
{
    public const string TenantId = "tenant_id";
}
