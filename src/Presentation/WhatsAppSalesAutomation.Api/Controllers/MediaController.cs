using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Media;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/media")]
[Authorize]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly ICurrentUserService _currentUser;

    public MediaController(IMediaService mediaService, ICurrentUserService currentUser)
    {
        _mediaService = mediaService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<MediaAssetDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _mediaService.GetPagedAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MediaAssetDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _mediaService.GetByIdAsync(id, cancellationToken));

    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<MediaAssetDto>> Upload(IFormFile file, IFormFile? thumbnail, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _mediaService.UploadAsync(stream, file.FileName, file.ContentType, file.Length, _currentUser.UserId, cancellationToken);
        result = await AttachThumbnailAsync(result, thumbnail, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>Adds a file the tenant already hosts by its link instead of uploading it.</summary>
    [HttpPost("from-url")]
    public async Task<ActionResult<MediaAssetDto>> AddFromUrl([FromBody] AddMediaFromUrlRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediaService.AddFromUrlAsync(request.Url, _currentUser.UserId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>Replaces the file of an existing entry (same id, so everything using it follows).</summary>
    [HttpPost("{id:guid}/replace")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<MediaAssetDto>> Replace(Guid id, IFormFile file, IFormFile? thumbnail, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _mediaService.ReplaceAsync(id, stream, file.FileName, file.ContentType, file.Length, cancellationToken);
        return Ok(await AttachThumbnailAsync(result, thumbnail, cancellationToken));
    }

    /// <summary>The still frame the browser cut from a video, sent in the same request. Only a video takes one, and a thumbnail
    /// that cannot be stored never fails an upload that already succeeded - the list just shows no picture for it.</summary>
    private async Task<MediaAssetDto> AttachThumbnailAsync(MediaAssetDto asset, IFormFile? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail is null || thumbnail.Length == 0 || !asset.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return asset;
        try
        {
            await using var stream = thumbnail.OpenReadStream();
            return await _mediaService.SetThumbnailAsync(asset.Id, stream, thumbnail.ContentType, thumbnail.Length, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return asset;
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force, CancellationToken cancellationToken)
    {
        await _mediaService.DeleteAsync(id, force, cancellationToken);
        return NoContent();
    }
}
