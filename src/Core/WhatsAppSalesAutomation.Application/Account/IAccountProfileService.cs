namespace WhatsAppSalesAutomation.Application.Account;

/// <summary>
/// The signed-in user's own profile - name, sign-in email, phone, display timezone and country. Self-service
/// and role-free on purpose: this is the one place any user, a PlatformSuperAdmin included, edits their own
/// account. It is not user administration (that is Users.IUserService, tenant Admin only, and it cannot
/// reach a PlatformSuperAdmin at all because of the tenant query filter on ApplicationUser).
///
/// Nothing here can change a role, a tenant, or the active flag - the fields that decide what an account may
/// do stay with the administrator screens.
/// </summary>
public interface IAccountProfileService
{
    Task<UserProfileDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves every editable field. Throws ValidationException when the requested email already
    /// belongs to another account, in any tenant.</summary>
    Task<UserProfileDto> UpdateAsync(UpdateUserProfileRequest request, CancellationToken cancellationToken = default);
}
