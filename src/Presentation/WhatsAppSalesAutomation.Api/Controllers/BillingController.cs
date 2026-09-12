using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Tenant-facing billing: the public plan catalog, choosing/switching a plan (Admin-only, same
/// reasoning as TenantSettingsController - this is billing, even if simulated right now), and the
/// tenant's own subscription/payment history. See IBillingService's own doc comment for why "choosing
/// a plan" is a simulated payment rather than a real gateway redirect at the moment.
/// </summary>
[ApiController]
[Route("api/v1/billing")]
public class BillingController : ControllerBase
{
    private readonly IBillingService _billingService;
    private readonly ITenantContext _tenantContext;

    public BillingController(IBillingService billingService, ITenantContext tenantContext)
    {
        _billingService = billingService;
        _tenantContext = tenantContext;
    }

    /// <summary>Anonymous callers (e.g. a not-yet-signed-up visitor) can pass <paramref name="country"/>
    /// to preview localized pricing; an authenticated tenant gets its own stored Tenant.CountryCode
    /// automatically and doesn't need to pass anything - see IBillingService.GetPlansAsync's own doc
    /// comment.</summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<PlanDto>>> GetPlans([FromQuery] string? country, CancellationToken cancellationToken)
        => Ok(await _billingService.GetPlansAsync(country, cancellationToken));

    [HttpGet("regions")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<RegionDto>>> GetRegions(CancellationToken cancellationToken)
        => Ok(await _billingService.GetRegionsAsync(cancellationToken));

    [HttpGet("subscription")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<SubscriptionDto?>> GetSubscription(CancellationToken cancellationToken)
        => Ok(await _billingService.GetSubscriptionForTenantAsync(RequireTenantId(), cancellationToken));

    /// <summary>Simulates paying for and switching to this plan, immediately - see
    /// IBillingService.ChoosePlanAsync's own doc comment.</summary>
    [HttpPost("plans/{planId:guid}/choose")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<SubscriptionDto>> ChoosePlan(Guid planId, CancellationToken cancellationToken)
        => Ok(await _billingService.ChoosePlanAsync(RequireTenantId(), planId, cancellationToken));

    [HttpGet("payments")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<PaymentDto>>> GetPayments(CancellationToken cancellationToken)
        => Ok(await _billingService.GetPaymentHistoryForTenantAsync(RequireTenantId(), cancellationToken));

    /// <summary>Every action above requires SuperAdmin/Admin, which - unlike PlatformSuperAdmin, whose
    /// role set never includes either - guarantees a real tenant is in scope; this just gives that
    /// guarantee a non-nullable type to hand IBillingService.</summary>
    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated billing request has no tenant in scope.");
}
