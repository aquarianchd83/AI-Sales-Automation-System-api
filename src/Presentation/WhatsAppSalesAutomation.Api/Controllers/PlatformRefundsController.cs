using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Refund requests from the Platform Admin Console - PlatformSuperAdmin-only. Every decision here is a
/// person's: nothing approves itself. Each action is audited.</summary>
[ApiController]
[Route("api/v1/platform")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformRefundsController : ControllerBase
{
    private readonly IRefundService _refunds;
    private readonly IBillingService _billing;
    private readonly IPlatformAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public PlatformRefundsController(IRefundService refunds, IBillingService billing, IPlatformAuditService audit, ICurrentUserService currentUser)
    {
        _refunds = refunds;
        _billing = billing;
        _audit = audit;
        _currentUser = currentUser;
    }

    /// <summary>One tenant's payments, newest first - what an operator picks from to refund directly.</summary>
    [HttpGet("tenants/{tenantId:guid}/payments")]
    public async Task<ActionResult<IReadOnlyList<PaymentDto>>> GetTenantPayments(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _billing.GetPaymentHistoryForTenantAsync(tenantId, cancellationToken));

    [HttpGet("refund-requests")]
    public async Task<ActionResult<PagedResult<RefundRequestDto>>> GetPaged([FromQuery] PlatformRefundQuery query, CancellationToken cancellationToken)
        => Ok(await _refunds.ListAsync(query, cancellationToken));

    [HttpPost("refund-requests/{id:guid}/approve")]
    public async Task<ActionResult<RefundRequestDto>> Approve(Guid id, [FromBody] ReviewRefundRequest review, CancellationToken cancellationToken)
    {
        var result = await _refunds.ApproveAsync(id, review, ActorUserId, cancellationToken);
        await _audit.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.RefundApproved, result.TenantId,
            details: $"Refund request {id}: {result.Status}, {result.CurrencySymbol}{result.RefundedLocalAmount:0.00} for {result.PaymentDescription}",
            cancellationToken: cancellationToken);
        return Ok(result);
    }

    [HttpPost("refund-requests/{id:guid}/reject")]
    public async Task<ActionResult<RefundRequestDto>> Reject(Guid id, [FromBody] ReviewRefundRequest review, CancellationToken cancellationToken)
    {
        var result = await _refunds.RejectAsync(id, review.Note, ActorUserId, cancellationToken);
        await _audit.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.RefundRejected, result.TenantId,
            details: $"Refund request {id}: {review.Note}", cancellationToken: cancellationToken);
        return Ok(result);
    }

    /// <summary>Refunds one payment on the operator's own initiative - outside the tenant switch and the
    /// tenant-facing time and usage limits, but never for money that isn't there.</summary>
    [HttpPost("tenants/{tenantId:guid}/payments/{paymentId:guid}/refund")]
    public async Task<ActionResult<RefundRequestDto>> RefundDirect(Guid tenantId, Guid paymentId, [FromBody] ReviewRefundRequest review, CancellationToken cancellationToken)
    {
        var result = await _refunds.RefundDirectAsync(tenantId, paymentId, review.Note, ActorUserId, cancellationToken);
        await _audit.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.RefundIssued, tenantId,
            details: $"Direct refund of payment {paymentId}: {result.Status} - {review.Note}", cancellationToken: cancellationToken);
        return Ok(result);
    }

    /// <summary>The per-tenant switch: on shows the tenant a way to request refunds, off hides it (and the server
    /// refuses the request either way).</summary>
    [HttpPut("tenants/{tenantId:guid}/refund-requests-enabled")]
    public async Task<IActionResult> SetEnabled(Guid tenantId, [FromBody] SetRefundRequestsEnabledRequest request, CancellationToken cancellationToken)
    {
        await _refunds.SetEnabledAsync(tenantId, request.Enabled, cancellationToken);
        await _audit.LogAsync(ActorUserId, ActorEmail, PlatformAuditActions.RefundRequestsToggled, tenantId,
            details: request.Enabled ? "Refund requests turned on" : "Refund requests turned off", cancellationToken: cancellationToken);
        return NoContent();
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}

public record SetRefundRequestsEnabledRequest(bool Enabled);
