using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The platform operators' alert inbox - background jobs that keep failing, and their recovery.
/// PlatformSuperAdmin-only; the tenant-facing equivalent is <c>BillingController</c>'s notifications.</summary>
[ApiController]
[Route("api/v1/platform/notifications")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformNotificationsController : ControllerBase
{
    private readonly IPlatformNotificationService _notifications;

    public PlatformNotificationsController(IPlatformNotificationService notifications) => _notifications = notifications;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformNotificationDto>>> GetRecent(CancellationToken cancellationToken)
        => Ok(await _notifications.GetRecentAsync(cancellationToken));

    [HttpPost("{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken cancellationToken)
    {
        await _notifications.AcknowledgeAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("acknowledge-all")]
    public async Task<IActionResult> AcknowledgeAll(CancellationToken cancellationToken)
    {
        await _notifications.AcknowledgeAllAsync(cancellationToken);
        return NoContent();
    }
}
