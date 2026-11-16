using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing.AutoPay;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Paying for a plan automatically each month through Razorpay UPI AutoPay - tenant Admin only, like the rest of billing. The tenant is
/// always the one on the token, never one named in the request, and the amount is always worked out on the server. Nothing here treats a
/// return from Razorpay's page as proof of anything: the mandate's state comes from Razorpay (webhooks, or asking it directly).
/// </summary>
[ApiController]
[Route("api/v1/billing/autopay")]
[Authorize(Roles = AppRoles.Admin)]
public class BillingAutoPayController : ControllerBase
{
    private readonly IAutoPayService _autoPay;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public BillingAutoPayController(IAutoPayService autoPay, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _autoPay = autoPay;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    /// <summary>Whether this tenant pays for plans through AutoPay - what the billing screen asks before choosing which button to draw.</summary>
    [HttpGet("availability")]
    public async Task<ActionResult<AutoPayAvailabilityDto>> GetAvailability(CancellationToken cancellationToken)
        => Ok(await _autoPay.GetAvailabilityAsync(RequireTenantId(), cancellationToken));

    /// <summary>Starts a mandate for one plan and returns Razorpay's page to authorise it on. 409 when the tenant cannot subscribe this way
    /// (already has a mandate, plan not sold in rupees, over the UPI limit) or Razorpay refused.</summary>
    [HttpPost("checkout")]
    public async Task<ActionResult<AutoPayCheckoutDto>> Checkout([FromBody] AutoPayCheckoutRequest request, CancellationToken cancellationToken)
        => Ok(await _autoPay.CheckoutAsync(RequireTenantId(), request.PlanId, _currentUser.UserId, cancellationToken));

    /// <summary>The tenant's mandate that matters now. 204 when it never started one.</summary>
    [HttpGet("subscription")]
    public async Task<ActionResult<AutoPaySubscriptionDto>> GetCurrent(CancellationToken cancellationToken)
    {
        var current = await _autoPay.GetCurrentAsync(RequireTenantId(), cancellationToken);
        return current is null ? NoContent() : Ok(current);
    }

    /// <summary>404 for an id that is not this tenant's.</summary>
    [HttpGet("subscriptions/{id:guid}")]
    public async Task<ActionResult<AutoPaySubscriptionDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _autoPay.GetAsync(RequireTenantId(), id, cancellationToken));

    /// <summary>Asks Razorpay where the mandate stands - for the screen the tenant lands on after authorising.</summary>
    [HttpPost("subscriptions/{id:guid}/refresh")]
    public async Task<ActionResult<AutoPaySubscriptionDto>> Refresh(Guid id, CancellationToken cancellationToken)
        => Ok(await _autoPay.RefreshAsync(RequireTenantId(), id, cancellationToken));

    /// <summary>Stops the mandate, now or at the end of the period already paid for. Paid-for access is never cut short.</summary>
    [HttpPost("subscriptions/{id:guid}/cancel")]
    public async Task<ActionResult<AutoPaySubscriptionDto>> Cancel(Guid id, [FromBody] CancelAutoPayRequest? request, CancellationToken cancellationToken)
        => Ok(await _autoPay.CancelAsync(RequireTenantId(), id, request?.CancelAtCycleEnd ?? true, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated billing request has no tenant in scope.");
}
