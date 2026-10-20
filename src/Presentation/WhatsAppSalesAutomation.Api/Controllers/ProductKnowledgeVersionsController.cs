using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Publishing the tenant's approved answers as a version, and the record of what was published. Tenant Admin only; every call is for the
/// caller's own tenant, and another tenant's version is "not found". A version is never changed or deleted: publishing again makes the next
/// one, and restoring an old one makes a new version that copies it.
/// </summary>
[ApiController]
[Route("api/v1/product-knowledge/versions")]
[Authorize(Roles = AppRoles.Admin)]
public class ProductKnowledgeVersionsController : ControllerBase
{
    private readonly IKnowledgePublishingService _publishing;
    private readonly IKnowledgeFileService _files;
    private readonly ITenantContext _tenantContext;

    public ProductKnowledgeVersionsController(IKnowledgePublishingService publishing, IKnowledgeFileService files, ITenantContext tenantContext)
    {
        _publishing = publishing;
        _files = files;
        _tenantContext = tenantContext;
    }

    /// <summary>What would be published now (and what is held back and why), and every version so far.</summary>
    [HttpGet]
    public async Task<ActionResult<KnowledgeVersionsOverviewDto>> Get(CancellationToken cancellationToken)
        => Ok(await _publishing.GetAsync(RequireTenantId(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnowledgeVersionDetailDto>> GetVersion(Guid id, CancellationToken cancellationToken)
        => Ok(await _publishing.GetVersionAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("publish")]
    public async Task<ActionResult<KnowledgeVersionDto>> Publish([FromBody] PublishRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var version = await _publishing.PublishAsync(tenantId, request ?? new PublishRequest(null), cancellationToken);
        await _files.SaveFinalCopyAsync(tenantId, version.Id, cancellationToken); // a record in the tenant's AWS folder; never fails the publish
        return Ok(version);
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<KnowledgeVersionDto>> Restore(Guid id, [FromBody] PublishRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var version = await _publishing.RestoreAsync(tenantId, id, request ?? new PublishRequest(null), cancellationToken);
        await _files.SaveFinalCopyAsync(tenantId, version.Id, cancellationToken);
        return Ok(version);
    }

    /// <summary>Talk to the AI as a customer would, answered only from the live published version. Nothing is stored or sent.</summary>
    [HttpPost("/api/v1/product-knowledge/test-chat")]
    public async Task<ActionResult<TestChatReply>> TestChat([FromBody] TestChatRequest request, [FromServices] IProductTestChat chat, CancellationToken cancellationToken)
        => Ok(await chat.AskAsync(RequireTenantId(), request ?? new TestChatRequest(null, null), cancellationToken));

    /// <summary>Builds the chat index of the current version again, after it failed. The version is not changed.</summary>
    [HttpPost("{id:guid}/reindex")]
    public async Task<ActionResult<KnowledgeVersionDto>> Reindex(Guid id, CancellationToken cancellationToken)
        => Ok(await _publishing.ReindexAsync(RequireTenantId(), id, cancellationToken));

    /// <summary>The working Draft file (plain text), clearly headed as not approved.</summary>
    [HttpGet("draft-file")]
    public async Task<IActionResult> DraftFile(CancellationToken cancellationToken)
        => ToFile(await _files.BuildDraftAsync(RequireTenantId(), cancellationToken));

    /// <summary>The Final file (plain text) of one published version: its approved answers only.</summary>
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> FinalFile(Guid id, CancellationToken cancellationToken)
        => ToFile(await _files.BuildFinalAsync(RequireTenantId(), id, cancellationToken));

    private FileContentResult ToFile(KnowledgeFileResult file)
    {
        Response.Headers["Cache-Control"] = "private, no-store";
        return File(file.Content, "text/plain; charset=utf-8", file.FileName);
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated product-knowledge request has no tenant in scope.");
}
