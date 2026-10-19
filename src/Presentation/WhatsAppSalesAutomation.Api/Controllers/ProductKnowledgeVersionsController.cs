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
    private readonly ITenantContext _tenantContext;

    public ProductKnowledgeVersionsController(IKnowledgePublishingService publishing, ITenantContext tenantContext)
    {
        _publishing = publishing;
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
        => Ok(await _publishing.PublishAsync(RequireTenantId(), request ?? new PublishRequest(null), cancellationToken));

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<KnowledgeVersionDto>> Restore(Guid id, [FromBody] PublishRequest request, CancellationToken cancellationToken)
        => Ok(await _publishing.RestoreAsync(RequireTenantId(), id, request ?? new PublishRequest(null), cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated product-knowledge request has no tenant in scope.");
}
