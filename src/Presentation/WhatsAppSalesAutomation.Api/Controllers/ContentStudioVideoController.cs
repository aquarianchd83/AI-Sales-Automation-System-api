using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Video;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Making videos from scripts in the AI Content Studio: start a render, watch it, cancel it, play the result. Admins and sales managers; every
/// call works on the calling tenant only (another tenant's id is "not found") and is refused unless the studio is on for the tenant. The finished
/// video is read back only through here - never by a storage link.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioVideoController : ControllerBase
{
    private readonly IVideoRenderService _renders;
    private readonly ITenantContext _tenantContext;

    public ContentStudioVideoController(IVideoRenderService renders, ITenantContext tenantContext)
    {
        _renders = renders;
        _tenantContext = tenantContext;
    }

    [HttpPost("versions/{id:guid}/render")]
    public async Task<ActionResult<RenderJobDto>> Start(Guid id, [FromBody] StartRenderRequest request, CancellationToken cancellationToken)
        => Ok(await _renders.StartAsync(RequireTenantId(), id, request ?? new StartRenderRequest(null, null), cancellationToken));

    [HttpGet("versions/{id:guid}/renders")]
    public async Task<ActionResult<IReadOnlyList<RenderJobDto>>> List(Guid id, CancellationToken cancellationToken)
        => Ok(await _renders.ListAsync(RequireTenantId(), id, cancellationToken));

    [HttpGet("render-jobs/{id:guid}")]
    public async Task<ActionResult<RenderJobDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _renders.GetAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("render-jobs/{id:guid}/cancel")]
    public async Task<ActionResult<RenderJobDto>> Cancel(Guid id, CancellationToken cancellationToken)
        => Ok(await _renders.CancelAsync(RequireTenantId(), id, cancellationToken));

    [HttpGet("render-jobs/{id:guid}/video")]
    public async Task<IActionResult> Video(Guid id, CancellationToken cancellationToken)
    {
        var video = await _renders.OpenVideoAsync(RequireTenantId(), id, cancellationToken);
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(video.Content, video.ContentType);
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content-studio request has no tenant in scope.");
}
