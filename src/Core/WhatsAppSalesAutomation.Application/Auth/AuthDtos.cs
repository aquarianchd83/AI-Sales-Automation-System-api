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
/// <param name="CountryCode">ISO 3166-1 alpha-2 (e.g. "US", "IN"), optional - drives which currency
/// IBillingService.GetPlansAsync later quotes this tenant's plan prices in (see
/// RegionalPricingCatalog). Omitted or unmatched falls back to USD, never an error.</param>
/// <param name="Timezone">IANA timezone id (Tenancy.TimeZoneCatalog), optional - drives which local
/// time Campaign.ScheduledStartAt is compared against for this tenant (see
/// ITenantTimeZoneProvider). Omitted falls back to India Standard Time, the same default every
/// tenant already had before per-tenant timezones existed.</param>
/// <param name="ProductName">Optional product or brand name - the one business detail signup asks for;
/// the rest are filled in afterwards on the tenant's Business Profile page.</param>
public record TenantSignUpRequest(
    string CompanyName,
    string? Slug,
    string FullName,
    string Email,
    string Password,
    string? CountryCode = null,
    string? Timezone = null,
    string? ProductName = null);

public record RefreshTokenRequest(string RefreshToken);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record TokenPairDto(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserDto User);
