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
    private readonly IPlanCostReportService _costReport;

    public PlatformBillingController(
        IPlatformBillingService billingService,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser,
        IPlanCostReportService costReport)
    {
        _costReport = costReport;
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

    /// <summary>The usage assumptions the cost report starts from - real averages where the platform has history.</summary>
    [HttpGet("plan-cost-defaults")]
    public async Task<ActionResult<PlanCostDefaultsDto>> GetPlanCostDefaults(CancellationToken cancellationToken)
        => Ok(await _costReport.GetDefaultsAsync(cancellationToken));

    /// <summary>The consolidated cost-and-margin report for a plan being designed. Nothing is saved.</summary>
    [HttpPost("plan-cost-report")]
    public async Task<ActionResult<PlanCostReportDto>> BuildPlanCostReport([FromBody] PlanCostReportRequest request, CancellationToken cancellationToken)
        => Ok(await _costReport.BuildAsync(request, cancellationToken));

    /// <summary>What a credit pack costs to serve and the price that leaves the wanted margin, from the Configuration charges. Nothing is saved.</summary>
    [HttpPost("credit-pack-cost")]
    public async Task<ActionResult<CreditPackCostReportDto>> BuildCreditPackCost([FromBody] CreditPackCostRequest request, CancellationToken cancellationToken)
        => Ok(await _costReport.BuildCreditPackAsync(request, cancellationToken));

    [HttpGet("subscriptions")]
    public async Task<ActionResult<PagedResult<PlatformSubscriptionListItemDto>>> GetSubscriptions(
        [FromQuery] PlatformSubscriptionQuery query, CancellationToken cancellationToken)
        => Ok(await _billingService.GetSubscriptionsAsync(query, cancellationToken));

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
