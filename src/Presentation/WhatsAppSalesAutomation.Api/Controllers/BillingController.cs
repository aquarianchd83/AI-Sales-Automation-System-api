using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
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
    private readonly IQuotaLedgerService _quota;
    private readonly IRefundService _refunds;
    private readonly ITenantBillingNoticeService _notices;
    private readonly ICurrentUserService _currentUser;
    private readonly ICountryAvailability _countries;

    public BillingController(
        IBillingService billingService,
        ITenantContext tenantContext,
        IQuotaLedgerService quota,
        IRefundService refunds,
        ITenantBillingNoticeService notices,
        ICurrentUserService currentUser,
        ICountryAvailability countries)
    {
        _countries = countries;
        _quota = quota;
        _refunds = refunds;
        _notices = notices;
        _currentUser = currentUser;
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
    public async Task<ActionResult<IReadOnlyList<RegionDto>>> GetRegions([FromQuery] string? include, CancellationToken cancellationToken)
    {
        // Only the countries the operator has switched on. `include` lets a screen that already has a value (a profile
        // whose country was switched off later) still show it, so the picker doesn't go blank.
        var disabled = await _countries.GetDisabledAsync(cancellationToken);
        var regions = await _billingService.GetRegionsAsync(cancellationToken);
        return Ok(regions
            .Where(r => !disabled.Contains(r.CountryCode) || string.Equals(r.CountryCode, include, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

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
    /// <summary>The tenant's prepaid balance per quota type, with the live grants behind each.</summary>
    [HttpGet("quota")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<QuotaBalanceDto>>> GetQuota(CancellationToken cancellationToken)
        => Ok(await _quota.GetBalancesAsync(RequireTenantId(), cancellationToken));

    /// <summary>The tenant's own quota ledger, newest first - purchases, allocations, consumption, expiry.</summary>
    [HttpGet("quota/ledger")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<PagedResult<QuotaLedgerEntryDto>>> GetQuotaLedger([FromQuery] QuotaLedgerQuery query, CancellationToken cancellationToken)
        => Ok(await _quota.GetLedgerAsync(RequireTenantId(), query, cancellationToken));

    [HttpGet("credit-packs")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<CreditPackDto>>> GetCreditPacks(CancellationToken cancellationToken)
        => Ok(await _billingService.GetCreditPacksAsync(RequireTenantId(), cancellationToken));

    [HttpPost("credit-packs/{packId:guid}/purchase")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<PaymentDto>> PurchaseCreditPack(Guid packId, CancellationToken cancellationToken)
        => Ok(await _billingService.PurchaseCreditPackAsync(RequireTenantId(), packId, cancellationToken));

    /// <summary>What this tenant is allowed to see - today just whether refund requests are switched on for it.
    /// The tenant UI asks this before drawing a refund button; the refund endpoints enforce it regardless.</summary>
    [HttpGet("capabilities")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<BillingCapabilitiesDto>> GetCapabilities(CancellationToken cancellationToken)
        => Ok(new BillingCapabilitiesDto(await _refunds.IsEnabledAsync(RequireTenantId(), cancellationToken)));

    [HttpGet("notifications")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<TenantNotificationDto>>> GetNotifications(CancellationToken cancellationToken)
        => Ok(await _notices.ListAsync(RequireTenantId(), cancellationToken));

    [HttpPost("notifications/{notificationId:guid}/acknowledge")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> AcknowledgeNotification(Guid notificationId, CancellationToken cancellationToken)
    {
        await _notices.AcknowledgeAsync(RequireTenantId(), notificationId, cancellationToken);
        return NoContent();
    }

    [HttpGet("alert-settings")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<BillingAlertSettingsDto>> GetAlertSettings(CancellationToken cancellationToken)
        => Ok(await _notices.GetSettingsAsync(RequireTenantId(), cancellationToken));

    [HttpPut("alert-settings")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<BillingAlertSettingsDto>> UpdateAlertSettings([FromBody] UpdateBillingAlertSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await _notices.UpdateSettingsAsync(RequireTenantId(), request, cancellationToken));

    /// <summary>403 unless the platform operator has switched refund requests on for this tenant.</summary>
    [HttpGet("payments/{paymentId:guid}/refund-eligibility")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<RefundEligibilityDto>> GetRefundEligibility(Guid paymentId, CancellationToken cancellationToken)
        => Ok(await _refunds.GetEligibilityAsync(RequireTenantId(), paymentId, cancellationToken));

    [HttpGet("refund-requests")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<RefundRequestDto>>> GetRefundRequests(CancellationToken cancellationToken)
        => Ok(await _refunds.ListForTenantAsync(RequireTenantId(), cancellationToken));

    /// <summary>Asks for a refund. Never automatic: it waits for a platform admin to approve or reject it.</summary>
    [HttpPost("refund-requests")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<RefundRequestDto>> RequestRefund([FromBody] RequestRefundRequest request, CancellationToken cancellationToken)
        => Ok(await _refunds.RequestAsync(
            RequireTenantId(), request.PaymentId, request.Reason,
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), cancellationToken));

    [HttpPost("refund-requests/{requestId:guid}/cancel")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<RefundRequestDto>> CancelRefundRequest(Guid requestId, CancellationToken cancellationToken)
        => Ok(await _refunds.CancelAsync(RequireTenantId(), requestId, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated billing request has no tenant in scope.");
}

public record BillingCapabilitiesDto(bool RefundRequestsEnabled);
