using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Account;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The signed-in user's own account. Deliberately role-free: every authenticated user has an account to
/// maintain, a PlatformSuperAdmin included - and a PlatformSuperAdmin cannot be reached through the
/// tenant-scoped Users screens at all (see IAccountProfileService's own doc comment).
///
/// Changing a password stays on AuthController where it already lives; this is the rest of the profile.
/// </summary>
[ApiController]
[Route("api/v1/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly IAccountProfileService _profileService;

    public AccountController(IAccountProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet("profile")]
    public async Task<ActionResult<UserProfileDto>> GetProfile(CancellationToken cancellationToken)
        => Ok(await _profileService.GetAsync(cancellationToken));

    /// <summary>Replaces every editable field. Changing the email changes what the user signs in with - see
    /// UpdateUserProfileRequest's own doc comment for what that does to a session already in flight.</summary>
    [HttpPut("profile")]
    public async Task<ActionResult<UserProfileDto>> UpdateProfile(
        [FromBody] UpdateUserProfileRequest request, CancellationToken cancellationToken)
        => Ok(await _profileService.UpdateAsync(request, cancellationToken));
}
