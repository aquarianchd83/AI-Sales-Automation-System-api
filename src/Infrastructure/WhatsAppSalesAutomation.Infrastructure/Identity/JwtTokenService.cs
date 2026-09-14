using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Infrastructure.Identity;

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> options)
    {
        _settings = options.Value;
    }

    // Time-boxed, per the Platform Admin Console spec's "audited, time-boxed" Impersonate action -
    // independent of the configurable normal AccessTokenMinutes so shortening/lengthening ordinary
    // sessions never accidentally changes how long a support session can run.
    private const int ImpersonationAccessTokenMinutes = 30;

    public JwtTokenResult GenerateAccessToken(ApplicationUser user, IReadOnlyList<string> roles) =>
        BuildToken(user, roles, TimeSpan.FromMinutes(_settings.AccessTokenMinutes), impersonatedByUserId: null);

    public JwtTokenResult GenerateImpersonationAccessToken(ApplicationUser targetUser, IReadOnlyList<string> roles, Guid actorUserId) =>
        BuildToken(targetUser, roles, TimeSpan.FromMinutes(ImpersonationAccessTokenMinutes), impersonatedByUserId: actorUserId);

    private JwtTokenResult BuildToken(ApplicationUser user, IReadOnlyList<string> roles, TimeSpan lifetime, Guid? impersonatedByUserId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // Null for a PlatformSuperAdmin (no single tenant) - CurrentUserService.TenantId/ITenantContext
        // both treat a missing/unparsable claim the same as "no tenant", so omitting it here is enough.
        if (user.TenantId is { } tenantId)
            claims.Add(new Claim(JwtClaimNames.TenantId, tenantId.ToString()));

        if (impersonatedByUserId is { } actorId)
            claims.Add(new Claim(JwtClaimNames.ImpersonatedBy, actorId.ToString()));

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAtUtc = DateTime.UtcNow.Add(lifetime);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new JwtTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    public JwtTokenResult GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        var token = Convert.ToBase64String(randomBytes);
        var expiresAtUtc = DateTime.UtcNow.AddDays(_settings.RefreshTokenDays);

        return new JwtTokenResult(token, expiresAtUtc);
    }

    public string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }
}
