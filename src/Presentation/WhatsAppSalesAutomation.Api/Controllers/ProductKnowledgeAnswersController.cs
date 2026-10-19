using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Drafted answers for the tenant's product questions, each with its evidence and an application-computed confidence: see everything,
/// draft from the sources (in the background), ask another suggestion for one question, ask a question of your own. Tenant Admin only; every
/// call is for the caller's own tenant, and another tenant's question or run is "not found".
/// </summary>
[ApiController]
[Route("api/v1/product-knowledge")]
[Authorize(Roles = AppRoles.Admin)]
public class ProductKnowledgeAnswersController : ControllerBase
{
    private readonly IKnowledgeDraftingService _drafting;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public ProductKnowledgeAnswersController(IKnowledgeDraftingService drafting, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _drafting = drafting;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    /// <summary>The questions with their current answers, the feature-to-benefit list, and the latest drafting run.</summary>
    [HttpGet("answers")]
    public async Task<ActionResult<KnowledgeAnswersOverviewDto>> Get(CancellationToken cancellationToken)
        => Ok(await _drafting.GetAsync(RequireTenantId(), cancellationToken));

    /// <summary>Drafts every question from the sources in the background. Returns the run to follow (or the one already going).</summary>
    [HttpPost("draft")]
    public async Task<ActionResult<DraftRunDto>> StartDraft(CancellationToken cancellationToken)
        => Accepted(await _drafting.StartRunAsync(RequireTenantId(), _currentUser.UserId, cancellationToken));

    [HttpGet("draft/{runId:guid}")]
    public async Task<ActionResult<DraftRunDto>> GetRun(Guid runId, CancellationToken cancellationToken)
        => Ok(await _drafting.GetRunAsync(RequireTenantId(), runId, cancellationToken));

    /// <summary>Another suggestion for one question.</summary>
    [HttpPost("questions/{id:guid}/draft")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Redraft(Guid id, CancellationToken cancellationToken)
        => Ok(await _drafting.RedraftAsync(RequireTenantId(), id, cancellationToken));

    /// <summary>The tenant's own question, answered from their sources at once.</summary>
    [HttpPost("questions")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Ask([FromBody] AskQuestionRequest request, CancellationToken cancellationToken)
        => Ok(await _drafting.AskAsync(RequireTenantId(), _currentUser.UserId, request, cancellationToken));

    [HttpDelete("questions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _drafting.DeleteQuestionAsync(RequireTenantId(), id, cancellationToken);
        return NoContent();
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated product-knowledge request has no tenant in scope.");
}
