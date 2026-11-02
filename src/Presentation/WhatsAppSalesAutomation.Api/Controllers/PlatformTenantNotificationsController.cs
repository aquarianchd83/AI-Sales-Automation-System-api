using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's list of the notifications sent to tenants, by channel (in-app, email, WhatsApp) and delivery status. PlatformSuperAdmin-only, read-only.
/// Not the operators' own alert inbox - that is PlatformNotificationsController.</summary>
[ApiController]
[Route("api/v1/platform/tenant-notifications")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformTenantNotificationsController : ControllerBase
{
    private readonly IPlatformTenantNotificationService _notifications;

    public PlatformTenantNotificationsController(IPlatformTenantNotificationService notifications) => _notifications = notifications;

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformTenantNotificationDto>>> GetPaged([FromQuery] PlatformTenantNotificationQuery query, CancellationToken cancellationToken)
        => Ok(await _notifications.GetPagedAsync(query, cancellationToken));

    /// <summary>Every kind of notice, by name, for the page's Type filter.</summary>
    [HttpGet("kinds")]
    public ActionResult<IReadOnlyList<string>> GetKinds() => Ok(_notifications.GetKinds());
}
