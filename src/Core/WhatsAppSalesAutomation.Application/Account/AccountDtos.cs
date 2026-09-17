namespace WhatsAppSalesAutomation.Application.Account;

/// <summary>
/// The signed-in user's own profile. <paramref name="Timezone"/> is always the effective value (the platform
/// default when the user has never chosen one), never blank, the same convention TenantProfileDto uses;
/// <paramref name="CountryCode"/> has no such default and is null until set.
///
/// <paramref name="Roles"/>, <paramref name="CreatedAt"/> and <paramref name="LastLoginAt"/> are read-only
/// facts about the account - a user cannot grant themselves a role here.
/// </summary>
public record UserProfileDto(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    string Timezone,
    string? CountryCode,
    IReadOnlyList<string> Roles,
    bool IsPlatformSuperAdmin,
    Guid? TenantId,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

/// <summary>
/// Body of PUT account/profile - replaces every editable field at once, so a null optional field clears it.
///
/// <paramref name="Email"/> is the account's sign-in identity: changing it changes what the user logs in
/// with (their Identity user name moves with it). The access token they are holding keeps the old email in
/// its claims until they sign in again, so anything claim-derived - notably the actor email recorded on
/// platform audit entries - shows the previous address until then.
/// </summary>
public record UpdateUserProfileRequest(
    string FullName,
    string Email,
    string? PhoneNumber,
    string Timezone,
    string? CountryCode);
