using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's cross-tenant user search (spec item #6) - PlatformSuperAdmin-only.
/// Support lookup ("which org is this email in"), not a CRUD screen - see IPlatformUserSearchService's
/// own doc comment.</summary>
[ApiController]
[Route("api/v1/platform/users")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformUsersController : ControllerBase
{
    private readonly IPlatformUserSearchService _userSearchService;

    public PlatformUsersController(IPlatformUserSearchService userSearchService)
    {
        _userSearchService = userSearchService;
    }

    /// <summary>Support reset of a tenant user's password - audited, and signs the user out everywhere.</summary>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        Guid id, [FromBody] ResetUserPasswordRequest request, [FromServices] ICurrentUserService currentUser, CancellationToken cancellationToken)
    {
        await _userSearchService.ResetPasswordAsync(
            id, request,
            currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."),
            currentUser.Email ?? string.Empty, cancellationToken);
        return NoContent();
    }

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<PlatformUserSearchResultDto>>> Search([FromQuery] string? search, CancellationToken cancellationToken)
        => Ok(await _userSearchService.SearchAsync(search, cancellationToken));
}
