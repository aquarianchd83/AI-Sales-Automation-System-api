using WhatsAppSalesAutomation.Application.Users;

namespace WhatsAppSalesAutomation.Application.Auth;

/// <summary>
/// <paramref name="Slug"/> is optional and purely a UX nicety: when the frontend knows which
/// workspace subdomain a login came in on, it can pass that slug and get a clearer "wrong workspace"
/// error instead of a confusing subsequent 403 - it plays no part in actually resolving which tenant
/// the login belongs to (the account's own TenantId does that) and none in enforcing data isolation
/// (the JWT's tenant claim does that, regardless of which subdomain the request came in on).
/// </summary>
public record LoginRequest(string Email, string Password, string? Slug = null);

/// <summary>Self-serve tenant signup: creates a new <c>Tenant</c> (on trial) and its first Admin user
/// in one call - see <c>AuthService.SignUpAsync</c>. <paramref name="Slug"/> is optional; when omitted
/// it's derived from <paramref name="CompanyName"/> and de-duplicated automatically.</summary>
public record TenantSignUpRequest(
    string CompanyName,
    string? Slug,
    string FullName,
    string Email,
    string Password);

public record RefreshTokenRequest(string RefreshToken);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record TokenPairDto(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserDto User);
