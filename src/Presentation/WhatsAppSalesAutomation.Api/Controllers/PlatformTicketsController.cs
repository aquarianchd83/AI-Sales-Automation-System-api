using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The product owner's tickets - work the system raises when something only the platform can fix has affected a tenant. PlatformSuperAdmin-only.</summary>
[ApiController]
[Route("api/v1/platform/tickets")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformTicketsController : ControllerBase
{
    private readonly IPlatformTicketService _tickets;

    public PlatformTicketsController(IPlatformTicketService tickets) => _tickets = tickets;

    /// <summary><c>?status=Open|InProgress|Resolved</c> narrows the list; without it everything is returned, unresolved first.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformTicketDto>>> Get([FromQuery] PlatformTicketStatus? status, CancellationToken cancellationToken)
        => Ok(await _tickets.GetAsync(status, cancellationToken));

    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<PlatformTicketDto>> SetStatus(Guid id, [FromBody] UpdatePlatformTicketStatusRequest request, CancellationToken cancellationToken)
        => Ok(await _tickets.SetStatusAsync(id, request.Status, cancellationToken));
}
