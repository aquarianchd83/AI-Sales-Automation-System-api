using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Subscriptions & Billing screen (spec item #3) -
/// PlatformSuperAdmin-only. Plan override (upgrade/downgrade) lives on PlatformTenantsController
/// since it acts on one tenant - see IPlatformTenantService.OverridePlanAsync. The plan catalog
/// itself (add/edit/retire) is managed here instead, since a plan is platform-global, not any one
/// tenant's - see PlanSeeder's own doc comment for why it no longer owns these fields.</summary>
[ApiController]
[Route("api/v1/platform/billing")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformBillingController : ControllerBase
{
    private readonly IPlatformBillingService _billingService;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformBillingController(
        IPlatformBillingService billingService,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser)
    {
        _billingService = billingService;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet("plans")]
    public async Task<ActionResult<IReadOnlyList<PlatformPlanDto>>> GetPlans(CancellationToken cancellationToken)
        => Ok(await _billingService.GetPlansAsync(cancellationToken));

    [HttpPost("plans")]
    public async Task<ActionResult<PlatformPlanDto>> CreatePlan([FromBody] CreatePlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _billingService.CreatePlanAsync(request, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.PlanCreated,
            details: $"Plan '{result.Code}' ({result.Name})", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(GetPlans), null, result);
    }

    [HttpPut("plans/{id:guid}")]
    public async Task<ActionResult<PlatformPlanDto>> UpdatePlan(Guid id, [FromBody] UpdatePlanRequest request, CancellationToken cancellationToken)
    {
        var result = await _billingService.UpdatePlanAsync(id, request, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.PlanUpdated,
            details: $"Plan '{result.Code}' ({result.Name})", cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Retires the plan - see IPlatformBillingService.DeactivatePlanAsync's own doc comment
    /// for why this is never a hard delete.</summary>
    [HttpDelete("plans/{id:guid}")]
    public async Task<IActionResult> DeactivatePlan(Guid id, CancellationToken cancellationToken)
    {
        await _billingService.DeactivatePlanAsync(id, cancellationToken);
        await _auditService.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.PlanDeactivated, details: id.ToString(), cancellationToken: cancellationToken);
        return NoContent();
    }

    /// <summary>Every credit pack, active and retired - the catalog tenants buy extra quota from.</summary>
    [HttpGet("credit-packs")]
    public async Task<ActionResult<IReadOnlyList<PlatformCreditPackDto>>> GetCreditPacks(CancellationToken cancellationToken)
        => Ok(await _billingService.GetCreditPacksAsync(cancellationToken));

    [HttpPost("credit-packs")]
    public async Task<ActionResult<PlatformCreditPackDto>> CreateCreditPack([FromBody] CreateCreditPackRequest request, CancellationToken cancellationToken)
    {
        var result = await _billingService.CreateCreditPackAsync(request, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.CreditPackCreated,
            details: $"Credit pack '{result.Name}' ({result.Units:0.##} {result.QuotaType}, {result.PriceCents} cents)", cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(GetCreditPacks), null, result);
    }

    [HttpPut("credit-packs/{id:guid}")]
    public async Task<ActionResult<PlatformCreditPackDto>> UpdateCreditPack(Guid id, [FromBody] UpdateCreditPackRequest request, CancellationToken cancellationToken)
    {
        var result = await _billingService.UpdateCreditPackAsync(id, request, cancellationToken);

        await _auditService.LogAsync(
            ActorUserId, ActorEmail, PlatformAuditActions.CreditPackUpdated,
            details: $"Credit pack '{result.Name}' ({result.Units:0.##} {result.QuotaType}, {result.PriceCents} cents, {(result.IsActive ? "active" : "retired")})", cancellationToken: cancellationToken);

        return Ok(result);
    }

    /// <summary>Retires the pack - never a hard delete, so past purchases keep pointing at it.</summary>
    [HttpDelete("credit-packs/{id:guid}")]
    public async Task<IActionResult> DeactivateCreditPack(Guid id, CancellationToken cancellationToken)
    {
        await _billingService.DeactivateCreditPackAsync(id, cancellationToken);
        await _auditService.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.CreditPackDeactivated, details: id.ToString(), cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpGet("subscriptions")]
    public async Task<ActionResult<PagedResult<PlatformSubscriptionListItemDto>>> GetSubscriptions(
        [FromQuery] PlatformSubscriptionQuery query, CancellationToken cancellationToken)
        => Ok(await _billingService.GetSubscriptionsAsync(query, cancellationToken));

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
