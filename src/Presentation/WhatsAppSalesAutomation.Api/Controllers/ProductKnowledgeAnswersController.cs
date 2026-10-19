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
    private readonly IKnowledgeVerificationService _verification;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public ProductKnowledgeAnswersController(
        IKnowledgeDraftingService drafting, IKnowledgeVerificationService verification, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _drafting = drafting;
        _verification = verification;
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

    // ----- the tenant's decisions ----------------------------------------------------------------------------------------------------

    /// <summary>Approves the current answer. A price, guarantee or weak answer needs <c>confirmed: true</c>; test data and answers with open conflicts are refused.</summary>
    [HttpPost("answers/{answerId:guid}/approve")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Approve(Guid answerId, [FromBody] ApproveAnswerRequest request, CancellationToken cancellationToken)
        => Ok(await _verification.ApproveAsync(RequireTenantId(), answerId, request ?? new ApproveAnswerRequest(false), cancellationToken));

    [HttpPost("answers/{answerId:guid}/reject")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Reject(Guid answerId, [FromBody] RejectAnswerRequest request, CancellationToken cancellationToken)
        => Ok(await _verification.RejectAsync(RequireTenantId(), answerId, request ?? new RejectAnswerRequest(null), cancellationToken));

    /// <summary>Takes an approved answer back for correction; the approval stays in the history.</summary>
    [HttpPost("answers/{answerId:guid}/reopen")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Reopen(Guid answerId, [FromBody] ReopenAnswerRequest request, CancellationToken cancellationToken)
        => Ok(await _verification.ReopenAsync(RequireTenantId(), answerId, request ?? new ReopenAnswerRequest(null), cancellationToken));

    /// <summary>The tenant's own answer - a correction, or what the sources lacked. A new version; what it replaces stays in the history.</summary>
    [HttpPost("questions/{id:guid}/edit")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Edit(Guid id, [FromBody] EditAnswerRequest request, CancellationToken cancellationToken)
        => Ok(await _verification.EditAsync(RequireTenantId(), id, request, cancellationToken));

    [HttpPost("questions/{id:guid}/unknown")]
    public async Task<ActionResult<KnowledgeQuestionDto>> Unknown(Guid id, CancellationToken cancellationToken)
        => Ok(await _verification.MarkUnknownAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("questions/{id:guid}/not-applicable")]
    public async Task<ActionResult<KnowledgeQuestionDto>> NotApplicable(Guid id, CancellationToken cancellationToken)
        => Ok(await _verification.MarkNotApplicableAsync(RequireTenantId(), id, cancellationToken));

    /// <summary>The audit trail of one question: every draft, approval, edit, rejection and conflict decision, newest first.</summary>
    [HttpGet("questions/{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<AnswerRevisionDto>>> History(Guid id, CancellationToken cancellationToken)
        => Ok(await _verification.HistoryAsync(RequireTenantId(), id, cancellationToken));

    /// <summary>Chooses between values the sources disagree about. Nothing else decides: not dates, not versions.</summary>
    [HttpPost("conflicts/{conflictId:guid}/resolve")]
    public async Task<ActionResult<KnowledgeQuestionDto>> ResolveConflict(Guid conflictId, [FromBody] ResolveConflictRequest request, CancellationToken cancellationToken)
        => Ok(await _verification.ResolveConflictAsync(RequireTenantId(), conflictId, request, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated product-knowledge request has no tenant in scope.");
}
