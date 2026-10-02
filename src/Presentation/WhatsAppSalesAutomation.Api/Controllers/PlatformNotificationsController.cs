using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
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

    /// <summary>The full inbox, paged - behind the bell's "More notifications" button. <c>?page=&amp;pageSize=&amp;search=&amp;unreadOnly=</c>.</summary>
    [HttpGet("history")]
    public async Task<ActionResult<PagedResult<PlatformNotificationDto>>> GetHistory(
        [FromQuery] PagedRequest request, [FromQuery] bool unreadOnly, CancellationToken cancellationToken)
        => Ok(await _notifications.GetHistoryAsync(request, unreadOnly, cancellationToken));

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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _notifications.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
