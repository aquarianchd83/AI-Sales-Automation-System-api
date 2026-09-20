using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's lead scoring rules, and the workings behind any one lead's score.
///
/// Admin-only for writes, same reasoning as the qualification schema: these rules decide which leads
/// the whole sales team treats as urgent. The breakdown is readable by anyone who works leads - a
/// score nobody can take apart is a score they learn to ignore.
/// </summary>
[ApiController]
[Route("api/v1/lead-scoring")]
[Authorize]
public class LeadScoringController : ControllerBase
{
    private readonly ILeadScoringAdminService _scoring;
    private readonly ICurrentUserService _currentUser;
    private readonly ITenantContext _tenantContext;

    public LeadScoringController(
        ILeadScoringAdminService scoring,
        ICurrentUserService currentUser,
        ITenantContext tenantContext)
    {
        _scoring = scoring;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
    }

    [HttpGet("rules")]
    public async Task<ActionResult<IReadOnlyList<LeadScoringRuleDto>>> GetRules(CancellationToken cancellationToken)
        => Ok(await _scoring.GetRulesAsync(cancellationToken));

    [HttpGet("rules/{id:guid}")]
    public async Task<ActionResult<LeadScoringRuleDto>> GetRule(Guid id, CancellationToken cancellationToken)
        => Ok(await _scoring.GetRuleAsync(id, cancellationToken));

    [HttpPost("rules")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadScoringRuleDto>> CreateRule(
        [FromBody] CreateLeadScoringRuleRequest request, CancellationToken cancellationToken)
    {
        var created = await _scoring.CreateRuleAsync(request, CurrentUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetRule), new { id = created.Id }, created);
    }

    [HttpPut("rules/{id:guid}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadScoringRuleDto>> UpdateRule(
        Guid id, [FromBody] UpdateLeadScoringRuleRequest request, CancellationToken cancellationToken)
        => Ok(await _scoring.UpdateRuleAsync(id, request, CurrentUserId(), cancellationToken));

    [HttpDelete("rules/{id:guid}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        await _scoring.DeleteRuleAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("rules/seed-defaults")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<LeadScoringRuleDto>>> SeedDefaults(CancellationToken cancellationToken)
        => Ok(await _scoring.SeedDefaultsAsync(CurrentTenantId(), cancellationToken));

    /// <summary>What the rule editor needs to build a rule: the rule types with what each one's match
    /// value means, the intent names, and this tenant's own field keys.</summary>
    [HttpGet("catalog")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadScoringCatalogDto>> GetCatalog(CancellationToken cancellationToken)
        => Ok(await _scoring.GetCatalogAsync(cancellationToken));

    [HttpGet("leads/{leadId:guid}/breakdown")]
    public async Task<ActionResult<LeadScoreBreakdownDto>> GetBreakdown(Guid leadId, CancellationToken cancellationToken)
        => Ok(await _scoring.GetBreakdownAsync(leadId, cancellationToken));

    private Guid CurrentUserId() =>
        _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");

    private Guid CurrentTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Request has no tenant in scope.");
}
