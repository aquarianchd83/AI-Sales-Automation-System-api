using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Platform Admin Console's Announcements (spec item #11) - a platform-wide banner broadcast to every
/// tenant admin. <see cref="GetActive"/> is deliberately open to any authenticated user of any tenant
/// (that's the whole point of a broadcast banner); every other action is PlatformSuperAdmin-only.
/// </summary>
[ApiController]
[Route("api/v1/announcements")]
[Authorize]
public class AnnouncementsController : ControllerBase
{
    private readonly IAnnouncementService _announcementService;
    private readonly ICurrentUserService _currentUser;

    public AnnouncementsController(IAnnouncementService announcementService, ICurrentUserService currentUser)
    {
        _announcementService = announcementService;
        _currentUser = currentUser;
    }

    /// <summary>Every tenant admin's view - currently active announcements only.</summary>
    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<AnnouncementDto>>> GetActive(CancellationToken cancellationToken)
        => Ok(await _announcementService.GetActiveAsync(cancellationToken));

    [HttpGet]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<IReadOnlyList<AnnouncementDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _announcementService.GetAllAsync(cancellationToken));

    [HttpPost]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<AnnouncementDto>> Create([FromBody] CreateAnnouncementRequest request, CancellationToken cancellationToken)
    {
        var result = await _announcementService.CreateAsync(request, ActorUserId, ActorEmail, cancellationToken);
        return Ok(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<AnnouncementDto>> Update(Guid id, [FromBody] UpdateAnnouncementRequest request, CancellationToken cancellationToken)
        => Ok(await _announcementService.UpdateAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _announcementService.DeleteAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
