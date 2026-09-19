using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// A tenant's request for money back on one <see cref="Payment"/>. The units that would be refunded are
/// taken out of the wallet (ledger entries of type RefundHold, keyed by this request's id) the moment the
/// request is made, so they cannot be spent while it waits. Approval keeps the held units gone and posts
/// the refund; rejection, cancellation or expiry gives them back.
///
/// Amounts are snapshotted at request time in both base USD cents and the tenant's own currency, like
/// <see cref="Payment"/>: a later rate change never moves a refund.
/// </summary>
public class RefundRequest : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid PaymentId { get; set; }

    public RefundStatus Status { get; set; } = RefundStatus.Requested;

    public string Reason { get; set; } = string.Empty;

    public Guid? RequestedByUserId { get; set; }

    /// <summary>What the rules allowed at the moment of the request - the ceiling for an approval.</summary>
    public int EligibleAmountCents { get; set; }

    public decimal EligibleLocalAmount { get; set; }

    /// <summary>What was actually refunded - set on approval, never above the eligible amount.</summary>
    public int? RefundedAmountCents { get; set; }

    public decimal? RefundedLocalAmount { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }

    public string? ReviewNote { get; set; }

    /// <summary>The negative <see cref="Payment"/> row this refund created.</summary>
    public Guid? RefundPaymentId { get; set; }

    /// <summary>The gateway's own reference for the refund - "Simulated" rows carry a generated one.</summary>
    public string? ProviderReference { get; set; }

    /// <summary>The gateway's error when Status is Failed.</summary>
    public string? FailureReason { get; set; }
}
