using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's side of the AI Content Studio's knowledge of its own business: the questionnaire it answers, and the business facts
/// that come out of it. Admins and sales managers fill in the questionnaire and manage facts; only an Admin may <i>verify</i> a fact,
/// because verification is the claim "this is true", and content may only assert verified facts. Every action is refused unless the
/// platform has the studio switched on for the tenant (see StudioGate), and works on the calling tenant only.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioQuestionnaireController : ControllerBase
{
    private readonly IQuestionnaireService _questionnaire;
    private readonly IBusinessFactService _facts;
    private readonly IKnowledgeService _knowledge;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public ContentStudioQuestionnaireController(
        IQuestionnaireService questionnaire, IBusinessFactService facts, IKnowledgeService knowledge, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _questionnaire = questionnaire;
        _facts = facts;
        _knowledge = knowledge;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    /// <summary>Whether the studio is on for this tenant. Open to everyone who can use the studio, unlike the Admin-only overview.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<StudioStatusDto>> GetStatus(CancellationToken cancellationToken)
        => Ok(new StudioStatusDto(await _questionnaire.IsEnabledAsync(RequireTenantId(), cancellationToken)));

    [HttpGet("questionnaire")]
    public async Task<ActionResult<QuestionnaireStateDto>> GetQuestionnaire(CancellationToken cancellationToken)
        => Ok(await _questionnaire.GetStateAsync(RequireTenantId(), cancellationToken));

    [HttpPut("questionnaire/answers")]
    public async Task<ActionResult<QuestionnaireStateDto>> SaveAnswers([FromBody] SaveQuestionnaireAnswersRequest request, CancellationToken cancellationToken)
        => Ok(await _questionnaire.SaveAnswersAsync(RequireTenantId(), request, _currentUser.UserId, cancellationToken));

    [HttpPost("questionnaire/complete")]
    public async Task<ActionResult<QuestionnaireCompletionDto>> Complete(CancellationToken cancellationToken)
        => Ok(await _questionnaire.CompleteAsync(RequireTenantId(), _currentUser.UserId, cancellationToken));

    [HttpPost("questionnaire/restart")]
    public async Task<ActionResult<QuestionnaireStateDto>> Restart(CancellationToken cancellationToken)
        => Ok(await _questionnaire.RestartAsync(RequireTenantId(), cancellationToken));

    [HttpGet("facts")]
    public async Task<ActionResult<PagedResult<BusinessFactDto>>> GetFacts([FromQuery] BusinessFactQuery query, CancellationToken cancellationToken)
        => Ok(await _facts.ListAsync(RequireTenantId(), query, cancellationToken));

    [HttpGet("facts/summary")]
    public async Task<ActionResult<BusinessFactSummaryDto>> GetFactSummary(CancellationToken cancellationToken)
        => Ok(await _facts.GetSummaryAsync(RequireTenantId(), cancellationToken));

    [HttpPost("facts")]
    public async Task<ActionResult<BusinessFactDto>> CreateFact([FromBody] SaveBusinessFactRequest request, CancellationToken cancellationToken)
        => Ok(await _facts.CreateAsync(RequireTenantId(), _currentUser.UserId, request, cancellationToken));

    [HttpPut("facts/{id:guid}")]
    public async Task<ActionResult<BusinessFactDto>> UpdateFact(Guid id, [FromBody] SaveBusinessFactRequest request, CancellationToken cancellationToken)
        => Ok(await _facts.UpdateAsync(RequireTenantId(), id, _currentUser.UserId, request, cancellationToken));

    /// <summary>Admin only: confirming a fact is true is the one thing the studio's content depends on.</summary>
    [HttpPost("facts/{id:guid}/verify")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<BusinessFactDto>> VerifyFact(Guid id, [FromQuery] bool reconfirm = false, CancellationToken cancellationToken = default)
        => Ok(await _facts.VerifyAsync(RequireTenantId(), id, _currentUser.UserId ?? throw new InvalidOperationException("A verification needs a signed-in user."), reconfirm, cancellationToken));

    [HttpPost("facts/{id:guid}/retire")]
    public async Task<ActionResult<BusinessFactDto>> RetireFact(Guid id, CancellationToken cancellationToken)
        => Ok(await _facts.RetireAsync(RequireTenantId(), id, _currentUser.UserId, cancellationToken));

    /// <summary>The newest changes to the tenant's answers, optionally for one question - who changed what, and when.</summary>
    [HttpGet("questionnaire/history")]
    public async Task<ActionResult<IReadOnlyList<AnswerRevisionDto>>> GetAnswerHistory([FromQuery] string? questionKey, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
        => Ok(await _questionnaire.GetHistoryAsync(RequireTenantId(), questionKey, take, cancellationToken));

    /// <summary>Adds proof or an end date to a fact without changing what it states.</summary>
    [HttpPut("facts/{id:guid}/details")]
    public async Task<ActionResult<BusinessFactDto>> UpdateFactDetails(Guid id, [FromBody] UpdateFactDetailsRequest request, CancellationToken cancellationToken)
        => Ok(await _facts.UpdateDetailsAsync(RequireTenantId(), id, _currentUser.UserId, request, cancellationToken));

    [HttpGet("facts/{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<BusinessFactRevisionDto>>> GetFactHistory(Guid id, CancellationToken cancellationToken)
        => Ok(await _facts.GetHistoryAsync(RequireTenantId(), id, cancellationToken));

    // ----- readiness and the missing-information workflow ----------------------------------------------------------------------------

    [HttpGet("knowledge/readiness")]
    public async Task<ActionResult<KnowledgeReadinessDto>> GetReadiness(CancellationToken cancellationToken)
        => Ok(await _knowledge.GetReadinessAsync(RequireTenantId(), cancellationToken));

    [HttpPost("knowledge/gaps/{key}/answer")]
    public async Task<ActionResult<KnowledgeReadinessDto>> AnswerGap(string key, [FromBody] AnswerGapRequest request, CancellationToken cancellationToken)
        => Ok(await _knowledge.AnswerGapAsync(RequireTenantId(), _currentUser.UserId, key, request, cancellationToken));

    [HttpPost("knowledge/gaps/{key}/skip")]
    public async Task<ActionResult<KnowledgeReadinessDto>> SkipGap(string key, CancellationToken cancellationToken)
        => Ok(await _knowledge.SkipGapAsync(RequireTenantId(), _currentUser.UserId, key, cancellationToken));

    [HttpPost("knowledge/gaps/{key}/defer")]
    public async Task<ActionResult<KnowledgeReadinessDto>> DeferGap(string key, [FromBody] DeferGapRequest request, CancellationToken cancellationToken)
        => Ok(await _knowledge.DeferGapAsync(RequireTenantId(), _currentUser.UserId, key, request, cancellationToken));

    [HttpPost("knowledge/gaps/{key}/reopen")]
    public async Task<ActionResult<KnowledgeReadinessDto>> ReopenGap(string key, CancellationToken cancellationToken)
        => Ok(await _knowledge.ReopenGapAsync(RequireTenantId(), key, cancellationToken));

    /// <summary>Checks sentences against the tenant's verified facts and flags the ones nothing supports.</summary>
    [HttpPost("knowledge/claims/check")]
    public async Task<ActionResult<ClaimCheckResultDto>> CheckClaims([FromBody] CheckClaimsRequest request, CancellationToken cancellationToken)
        => Ok(await _knowledge.CheckClaimsAsync(RequireTenantId(), request, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content studio request has no tenant in scope.");
}

/// <summary>Role sets for the studio's tenant-facing endpoints, as the constant strings <c>[Authorize]</c> needs.</summary>
public static class StudioRoles
{
    public const string Contributors = AppRoles.Admin + "," + AppRoles.SalesManager;
}

public record StudioStatusDto(bool Enabled);
