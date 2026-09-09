using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Subscriptions & Billing screen (spec item #3) -
/// PlatformSuperAdmin-only. Plan override (upgrade/downgrade) lives on PlatformTenantsController
/// since it acts on one tenant - see IPlatformTenantService.OverridePlanAsync.</summary>
[ApiController]
[Route("api/v1/platform/billing")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformBillingController : ControllerBase
{
    private readonly IPlatformBillingService _billingService;

    public PlatformBillingController(IPlatformBillingService billingService)
    {
        _billingService = billingService;
    }

    [HttpGet("plans")]
    public async Task<ActionResult<IReadOnlyList<PlatformPlanDto>>> GetPlans(CancellationToken cancellationToken)
        => Ok(await _billingService.GetPlansAsync(cancellationToken));

    [HttpGet("subscriptions")]
    public async Task<ActionResult<PagedResult<PlatformSubscriptionListItemDto>>> GetSubscriptions(
        [FromQuery] PlatformSubscriptionQuery query, CancellationToken cancellationToken)
        => Ok(await _billingService.GetSubscriptionsAsync(query, cancellationToken));
}
