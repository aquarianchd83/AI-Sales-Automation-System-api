using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's sources for product knowledge: upload documents, add the website, follow how each is read, retry, re-scan, remove, and
/// download the original or the extracted text. Tenant Admin only. Every call is for the caller's own tenant; another tenant's source is
/// "not found". Files are never given out as storage links - they come back through these endpoints, after the tenant check.
/// </summary>
[ApiController]
[Route("api/v1/product-knowledge/sources")]
[Authorize(Roles = AppRoles.Admin)]
public class ProductKnowledgeSourcesController : ControllerBase
{
    /// <summary>The request body limit: the platform's file limit is checked inside, against its current setting, up to this ceiling.</summary>
    private const long UploadCeilingBytes = 210L * 1024 * 1024;

    private readonly IKnowledgeSourceService _sources;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public ProductKnowledgeSourcesController(IKnowledgeSourceService sources, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _sources = sources;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<KnowledgeSourcesOverviewDto>> List(CancellationToken cancellationToken)
        => Ok(await _sources.ListAsync(RequireTenantId(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnowledgeSourceDetailDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sources.GetAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("documents")]
    [RequestSizeLimit(UploadCeilingBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadCeilingBytes)]
    public async Task<ActionResult<KnowledgeSourceDto>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Choose a file to upload." });

        await using var stream = file.OpenReadStream();
        var source = await _sources.UploadAsync(RequireTenantId(), _currentUser.UserId, file.FileName, file.ContentType, stream, file.Length, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = source.Id }, source);
    }

    [HttpPost("websites")]
    public async Task<ActionResult<KnowledgeSourceDto>> AddWebsite([FromBody] AddWebsiteSourceRequest request, CancellationToken cancellationToken)
    {
        var source = await _sources.AddWebsiteAsync(RequireTenantId(), _currentUser.UserId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = source.Id }, source);
    }

    /// <summary>Reads a failed source again, or re-scans a website.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult<KnowledgeSourceDto>> Retry(Guid id, CancellationToken cancellationToken)
        => Ok(await _sources.RetryAsync(RequireTenantId(), id, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _sources.DeleteAsync(RequireTenantId(), id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/original")]
    public async Task<IActionResult> DownloadOriginal(Guid id, CancellationToken cancellationToken)
    {
        var file = await _sources.OpenFileAsync(RequireTenantId(), id, extracted: false, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("{id:guid}/text")]
    public async Task<IActionResult> DownloadText(Guid id, CancellationToken cancellationToken)
    {
        var file = await _sources.OpenFileAsync(RequireTenantId(), id, extracted: true, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated product-knowledge request has no tenant in scope.");
}
