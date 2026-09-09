using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Tenant-facing billing: the public plan catalog, and (SuperAdmin/Admin-only, same reasoning as
/// TenantSettingsController - this is real money) starting a Checkout/Billing Portal session and
/// checking the tenant's current subscription. Stripe's own webhook (activation, cancellation,
/// payment failure) is handled entirely separately by StripeWebhooksController - nothing here ever
/// writes Subscription/Tenant.Status itself.
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

    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<PlanDto>>> GetPlans(CancellationToken cancellationToken)
        => Ok(await _billingService.GetPlansAsync(cancellationToken));

    [HttpGet("subscription")]
    [Authorize(Roles = $"{AppRoles.SuperAdmin},{AppRoles.Admin}")]
    public async Task<ActionResult<SubscriptionDto?>> GetSubscription(CancellationToken cancellationToken)
        => Ok(await _billingService.GetSubscriptionForTenantAsync(RequireTenantId(), cancellationToken));

    [HttpPost("checkout")]
    [Authorize(Roles = $"{AppRoles.SuperAdmin},{AppRoles.Admin}")]
    public async Task<ActionResult<BillingSessionUrlDto>> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionRequest request, CancellationToken cancellationToken)
    {
        var url = await _billingService.CreateCheckoutSessionAsync(
            RequireTenantId(), request.PlanId, request.SuccessUrl, request.CancelUrl, cancellationToken);
        return Ok(new BillingSessionUrlDto(url));
    }

    [HttpPost("portal")]
    [Authorize(Roles = $"{AppRoles.SuperAdmin},{AppRoles.Admin}")]
    public async Task<ActionResult<BillingSessionUrlDto>> CreateBillingPortalSession(
        [FromBody] CreateBillingPortalSessionRequest request, CancellationToken cancellationToken)
    {
        var url = await _billingService.CreateBillingPortalSessionAsync(RequireTenantId(), request.ReturnUrl, cancellationToken);
        return Ok(new BillingSessionUrlDto(url));
    }

    /// <summary>Every action above requires SuperAdmin/Admin, which - unlike PlatformSuperAdmin, whose
    /// role set never includes either - guarantees a real tenant is in scope; this just gives that
    /// guarantee a non-nullable type to hand IBillingService.</summary>
    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated billing request has no tenant in scope.");
}
