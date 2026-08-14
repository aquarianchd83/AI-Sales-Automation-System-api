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
    public async Task<ActionResult<MediaAssetDto>> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("A non-empty file is required.");

        await using var stream = file.OpenReadStream();
        var result = await _mediaService.UploadAsync(stream, file.FileName, file.ContentType, file.Length, _currentUser.UserId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force, CancellationToken cancellationToken)
    {
        await _mediaService.DeleteAsync(id, force, cancellationToken);
        return NoContent();
    }
}
