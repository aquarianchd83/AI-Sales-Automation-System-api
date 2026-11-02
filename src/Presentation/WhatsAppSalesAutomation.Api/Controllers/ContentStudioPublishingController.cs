using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Publishing from the AI Content Studio: connecting a YouTube channel and sending approved videos to it. <b>Admin only</b>, like connecting the
/// ad account - it speaks for the business in public. Every call works on the calling tenant only (another tenant's id is "not found"), is
/// refused unless the studio is on for the tenant, and the approval gate is enforced in the service, not just on the screen. Tokens are never
/// returned by any of these endpoints.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio")]
[Authorize(Roles = AppRoles.Admin)]
public class ContentStudioPublishingController : ControllerBase
{
    private readonly IPublishingService _publishing;
    private readonly ITenantContext _tenantContext;

    public ContentStudioPublishingController(IPublishingService publishing, ITenantContext tenantContext)
    {
        _publishing = publishing;
        _tenantContext = tenantContext;
    }

    [HttpGet("connections")]
    public async Task<ActionResult<ConnectionsOverviewDto>> Connections(CancellationToken cancellationToken)
        => Ok(new ConnectionsOverviewDto(_publishing.YouTubeAvailable, await _publishing.ListConnectionsAsync(RequireTenantId(), cancellationToken)));

    [HttpGet("connections/youtube/connect-url")]
    public async Task<ActionResult<ConnectUrlDto>> ConnectUrl([FromQuery] string redirectUri, CancellationToken cancellationToken)
        => Ok(await _publishing.GetConnectUrlAsync(RequireTenantId(), redirectUri, cancellationToken));

    [HttpPost("connections/youtube/connect")]
    public async Task<ActionResult<ConnectionDto>> Connect([FromBody] CompleteConnectRequest request, CancellationToken cancellationToken)
        => Ok(await _publishing.ConnectAsync(RequireTenantId(), request, cancellationToken));

    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken cancellationToken)
    {
        await _publishing.DisconnectAsync(RequireTenantId(), id, cancellationToken);
        return NoContent();
    }

    [HttpPost("versions/{id:guid}/publish")]
    public async Task<ActionResult<PublicationDto>> Publish(Guid id, [FromBody] PublishVideoRequest request, CancellationToken cancellationToken)
        => Ok(await _publishing.PublishAsync(RequireTenantId(), id, request, cancellationToken));

    [HttpGet("versions/{id:guid}/publications")]
    public async Task<ActionResult<IReadOnlyList<PublicationDto>>> Publications(Guid id, CancellationToken cancellationToken)
        => Ok(await _publishing.ListAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("publications/{id:guid}/cancel")]
    public async Task<ActionResult<PublicationDto>> Cancel(Guid id, CancellationToken cancellationToken)
        => Ok(await _publishing.CancelAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("publications/{id:guid}/retry")]
    public async Task<ActionResult<PublicationDto>> Retry(Guid id, CancellationToken cancellationToken)
        => Ok(await _publishing.RetryAsync(RequireTenantId(), id, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content-studio request has no tenant in scope.");
}

/// <param name="YouTubeAvailable">False when the platform has no Google OAuth client set up: Connect is then not offered.</param>
public sealed record ConnectionsOverviewDto(bool YouTubeAvailable, IReadOnlyList<ConnectionDto> Connections);
