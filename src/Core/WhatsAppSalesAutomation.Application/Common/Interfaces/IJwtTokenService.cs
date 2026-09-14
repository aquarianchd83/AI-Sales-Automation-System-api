using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

public interface IJwtTokenService
{
    JwtTokenResult GenerateAccessToken(ApplicationUser user, IReadOnlyList<string> roles);

    /// <summary>
    /// A short-lived access token for <paramref name="targetUser"/>, carrying an
    /// <c>impersonated_by</c> claim identifying <paramref name="actorUserId"/> (the PlatformSuperAdmin
    /// running the support session). Deliberately has no paired refresh token - a support session ends
    /// on its own once it expires rather than silently renewing forever, which is the "time-boxed" half
    /// of the Platform Admin Console's Impersonate action.
    /// </summary>
    JwtTokenResult GenerateImpersonationAccessToken(ApplicationUser targetUser, IReadOnlyList<string> roles, Guid actorUserId);

    JwtTokenResult GenerateRefreshToken();

    /// <summary>SHA-256 hash of a raw token, used as the persisted lookup key - raw tokens are never stored.</summary>
    string HashToken(string rawToken);
}

public record JwtTokenResult(string Token, DateTime ExpiresAtUtc);
