using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The Story studio's video requests from every tenant: the platform admin writes the script and attaches the finished video, which lands in the
/// tenant's folder in the bucket and tells the tenant in-app. PlatformSuperAdmin only.
/// </summary>
[ApiController]
[Route("api/v1/platform/content-studio/video-requests")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformStoryVideoRequestsController : ControllerBase
{
    private readonly IStoryVideoRequestService _requests;
    private readonly IPlatformAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public PlatformStoryVideoRequestsController(IStoryVideoRequestService requests, IPlatformAuditService audit, ICurrentUserService currentUser)
    {
        _requests = requests;
        _audit = audit;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformStoryVideoRequestRowDto>>> List([FromQuery] StoryVideoRequestQuery query, CancellationToken ct)
        => Ok(await _requests.ListAllAsync(query, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlatformStoryVideoRequestDetailDto>> Get(Guid id, CancellationToken ct)
        => Ok(await _requests.GetAsync(id, ct));

    /// <summary>Writes the script for the tenant. Not charged again: the video's price, taken when the request was raised, covers it.</summary>
    [HttpPost("{id:guid}/script")]
    public async Task<ActionResult<PlatformStoryVideoRequestDetailDto>> GenerateScript(Guid id, CancellationToken ct)
    {
        var result = await _requests.GenerateScriptAsync(id, _currentUser.UserId, ct);
        await LogAsync(PlatformAuditActions.StoryScriptGenerated, $"Wrote the script for \"{result.Title}\" ({result.TenantName})", ct);
        return Ok(result);
    }

    /// <summary>Uploads the finished video: stored under the tenant's folder, the link recorded, the tenant told in-app.</summary>
    [HttpPost("{id:guid}/video")]
    [RequestSizeLimit(StoryVideoRequestService.MaxVideoBytes + 1_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = StoryVideoRequestService.MaxVideoBytes + 1_000_000)]
    public async Task<ActionResult<PlatformStoryVideoRequestDetailDto>> UploadVideo(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("A non-empty video file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _requests.AttachVideoAsync(id, _currentUser.UserId, stream, file.FileName, file.ContentType, file.Length, ct);
        await LogAsync(PlatformAuditActions.StoryVideoAttached, $"Attached a video to \"{result.Title}\" ({result.TenantName})", ct);
        return Ok(result);
    }

    /// <summary>Declines the request (the video cannot be made): the credits go back to the tenant, who is told why.</summary>
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<PlatformStoryVideoRequestDetailDto>> Reject(Guid id, [FromBody] RejectStoryVideoRequest request, CancellationToken ct)
    {
        var result = await _requests.RejectAsync(id, _currentUser.UserId, request.Reason, ct);
        await LogAsync(PlatformAuditActions.StoryVideoRejected, $"Declined the video request \"{result.Title}\" ({result.TenantName}) and returned its credits", ct);
        return Ok(result);
    }

    private Task LogAsync(string action, string details, CancellationToken ct) =>
        _audit.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, action,
            details: details, cancellationToken: ct);
}
