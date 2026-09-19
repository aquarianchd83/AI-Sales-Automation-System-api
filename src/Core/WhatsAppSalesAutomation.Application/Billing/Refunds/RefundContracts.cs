using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing.Refunds;

public record RefundGatewayResult(bool Success, string? Reference, string? Error);

/// <summary>Returns money to the payer. Simulated today; Razorpay's refund API drops in behind this without
/// the refund rules changing. <paramref name="idempotencyKey"/> makes a retry safe on a real gateway.</summary>
public interface IRefundGateway
{
    string Name { get; }

    Task<RefundGatewayResult> RefundAsync(Payment payment, int amountCents, decimal localAmount, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Why a payment can or can't be refunded right now, and for how much. The amount is what the rules
/// allow - the ceiling an operator's approval can't exceed.</summary>
public record RefundEligibilityDto(
    Guid PaymentId,
    bool Eligible,
    string? Reason,
    int AmountCents,
    decimal LocalAmount,
    string CurrencyCode,
    string CurrencySymbol,
    DateTime? WindowEndsAtUtc);

public record RefundRequestDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    Guid PaymentId,
    string PaymentDescription,
    RefundStatus Status,
    string Reason,
    int EligibleAmountCents,
    decimal EligibleLocalAmount,
    string CurrencyCode,
    string CurrencySymbol,
    int? RefundedAmountCents,
    decimal? RefundedLocalAmount,
    string? ReviewNote,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? ReviewedAtUtc);

public record RequestRefundRequest(Guid PaymentId, string Reason);

public record ReviewRefundRequest(string Note, int? ApprovedAmountCents = null);

public record PlatformRefundQuery : PagedRequest
{
    public RefundStatus? Status { get; init; }

    public Guid? TenantId { get; init; }
}

public interface IRefundService
{
    // ---- Tenant side. Everything here needs the tenant's RefundRequestsEnabled switch on.

    Task<RefundEligibilityDto> GetEligibilityAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken = default);

    Task<RefundRequestDto> RequestAsync(Guid tenantId, Guid paymentId, string reason, Guid requestedByUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefundRequestDto>> ListForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<RefundRequestDto> CancelAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>Whether this tenant currently sees refunds at all - what the tenant UI asks before drawing a button.</summary>
    Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken = default);

    // ---- Platform side. Never gated by the tenant switch: an operator can always refund.

    Task SetEnabledAsync(Guid tenantId, bool enabled, CancellationToken cancellationToken = default);

    Task<PagedResult<RefundRequestDto>> ListAsync(PlatformRefundQuery query, CancellationToken cancellationToken = default);

    /// <summary>Approves (in full, or for <see cref="ReviewRefundRequest.ApprovedAmountCents"/> if less) and
    /// pays it out. A Failed request may be approved again to retry.</summary>
    Task<RefundRequestDto> ApproveAsync(Guid requestId, ReviewRefundRequest review, Guid reviewerUserId, CancellationToken cancellationToken = default);

    Task<RefundRequestDto> RejectAsync(Guid requestId, string note, Guid reviewerUserId, CancellationToken cancellationToken = default);

    /// <summary>An operator-initiated refund of one payment - the same rules and the same ledger, no tenant
    /// request and no switch. Approved in the same step.</summary>
    Task<RefundRequestDto> RefundDirectAsync(Guid tenantId, Guid paymentId, string reason, Guid reviewerUserId, CancellationToken cancellationToken = default);

    /// <summary>Closes requests nobody answered within the policy window and returns their held units.</summary>
    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);
}
