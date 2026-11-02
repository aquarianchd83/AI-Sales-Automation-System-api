using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Assets;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The pictures of the AI Content Studio: the tenant's library (uploads and screenshots, each with the tenant's confirmation that they may
/// use it), stock photo search, and choosing a picture for a scene. Admins and sales managers; every call works on the calling tenant only
/// (another tenant's id is "not found") and is refused unless the studio is on for the tenant. Files are read back only through here -
/// never by a storage link.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioAssetsController : ControllerBase
{
    /// <summary>A little over the picture limit, for the form's own overhead; the service enforces the real limit on the bytes it reads.</summary>
    private const long UploadCeilingBytes = StudioAssetService.MaxImageBytes + 512 * 1024;

    private readonly IStudioAssetService _assets;
    private readonly ITenantContext _tenantContext;

    public ContentStudioAssetsController(IStudioAssetService assets, ITenantContext tenantContext)
    {
        _assets = assets;
        _tenantContext = tenantContext;
    }

    [HttpGet("assets")]
    public async Task<ActionResult<IReadOnlyList<AssetDto>>> List([FromQuery] string? search, CancellationToken cancellationToken)
        => Ok(await _assets.ListAsync(RequireTenantId(), search, cancellationToken));

    /// <summary>Multipart: <c>file</c>, <c>kind</c> (Upload or Screenshot), the two confirmations, and optional name, description and tags.</summary>
    [HttpPost("assets")]
    [RequestSizeLimit(UploadCeilingBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadCeilingBytes)]
    public async Task<ActionResult<AssetDto>> Upload(
        IFormFile? file, [FromForm] string? kind, [FromForm] string? name, [FromForm] string? description, [FromForm] string? tags,
        [FromForm] bool rightsConfirmed, [FromForm] bool personalDataChecked, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Choose a picture to upload." });
        if (!Enum.TryParse<StudioAssetKind>(kind, ignoreCase: true, out var parsed))
            parsed = StudioAssetKind.Upload;

        await using var stream = file.OpenReadStream();
        var asset = await _assets.UploadAsync(RequireTenantId(), new UploadAssetRequest(stream, file.Length, file.FileName, parsed, name, description, tags, rightsConfirmed, personalDataChecked), cancellationToken);
        return Ok(asset);
    }

    [HttpGet("assets/{id:guid}/content")]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        var file = await _assets.OpenAsync(RequireTenantId(), id, cancellationToken);
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType); // inline, with no file name: it is shown, not saved
    }

    [HttpDelete("assets/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _assets.DeleteAsync(RequireTenantId(), id, cancellationToken);
        return NoContent();
    }

    /// <summary>Ranked pictures for a scene; <c>includeStock</c> also searches the stock library, with <c>query</c> or the scene's own picture note.</summary>
    [HttpGet("scenes/{id:guid}/assets/candidates")]
    public async Task<ActionResult<AssetCandidatesDto>> Candidates(Guid id, [FromQuery] bool includeStock, [FromQuery] string? query, CancellationToken cancellationToken)
        => Ok(await _assets.CandidatesAsync(RequireTenantId(), id, includeStock, query, cancellationToken));

    [HttpPut("scenes/{id:guid}/asset")]
    public async Task<ActionResult<AssetSummaryDto>> Choose(Guid id, [FromBody] ChooseAssetRequest request, CancellationToken cancellationToken)
        => Ok(await _assets.ChooseAsync(RequireTenantId(), id, request, cancellationToken));

    [HttpDelete("scenes/{id:guid}/asset")]
    public async Task<IActionResult> Clear(Guid id, CancellationToken cancellationToken)
    {
        await _assets.ClearAsync(RequireTenantId(), id, cancellationToken);
        return NoContent();
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content-studio request has no tenant in scope.");
}
