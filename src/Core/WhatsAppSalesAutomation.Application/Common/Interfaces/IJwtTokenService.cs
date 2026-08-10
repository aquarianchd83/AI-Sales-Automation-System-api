using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

public interface IJwtTokenService
{
    JwtTokenResult GenerateAccessToken(ApplicationUser user, IReadOnlyList<string> roles);

    JwtTokenResult GenerateRefreshToken();

    /// <summary>SHA-256 hash of a raw token, used as the persisted lookup key - raw tokens are never stored.</summary>
    string HashToken(string rawToken);
}

public record JwtTokenResult(string Token, DateTime ExpiresAtUtc);
