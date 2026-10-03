using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The platform's own media library - PlatformSuperAdmin-only. Images for the WhatsApp notice templates. Not a tenant's library (/api/v1/media).</summary>
[ApiController]
[Route("api/v1/platform/media")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformMediaController : ControllerBase
{
    private readonly IPlatformMediaService _media;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformMediaController(IPlatformMediaService media, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _media = media;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MediaAssetDto>>> GetAll([FromQuery] string? search, CancellationToken cancellationToken)
        => Ok(await _media.GetAllAsync(search, cancellationToken));

    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<MediaAssetDto>> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _media.UploadAsync(stream, file.FileName, file.ContentType, file.Length, _currentUser.UserId, cancellationToken);
        await LogAsync(PlatformAuditActions.PlatformMediaUploaded, $"Uploaded \"{result.FileName}\" to the platform media library", cancellationToken);
        return Ok(result);
    }

    /// <summary>Replaces the file of an existing entry (same id, so every template using it follows).</summary>
    [HttpPost("{id:guid}/replace")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<MediaAssetDto>> Replace(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _media.ReplaceAsync(id, stream, file.FileName, file.ContentType, file.Length, cancellationToken);
        await LogAsync(PlatformAuditActions.PlatformMediaReplaced, $"Replaced the platform media file with \"{result.FileName}\"", cancellationToken);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _media.DeleteAsync(id, cancellationToken);
        await LogAsync(PlatformAuditActions.PlatformMediaDeleted, "Deleted a file from the platform media library", cancellationToken);
        return NoContent();
    }

    private Task LogAsync(string action, string details, CancellationToken cancellationToken) =>
        _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, action,
            details: details, cancellationToken: cancellationToken);
}
