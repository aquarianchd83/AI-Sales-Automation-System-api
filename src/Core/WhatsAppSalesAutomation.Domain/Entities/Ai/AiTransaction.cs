using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Ai;

/// <summary>
/// The audit record of one metered AI operation - authorised, completed, failed-and-refunded, or refused. Credit
/// usage (this row), purchase (a Payment and its grant), consumption and refund (the quota ledger's Consumption and
/// ConsumptionReversal entries, linked by <see cref="OperationKey"/>) stay distinct records; this row is the one that
/// ties an AI request to the credits it cost. Provider and model are text snapshots so changing the platform's
/// provider never orphans history.
/// </summary>
public class AiTransaction : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The subscription the operation ran under. Null for a trial that never chose a plan.</summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>What was asked for, e.g. "ConversationReply".</summary>
    public string Operation { get; set; } = string.Empty;

    public AiUsageSource Source { get; set; }

    public AiTransactionStatus Status { get; set; }

    public AiDenialReason DenialReason { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Ties this row to its quota-ledger entries. Unique per attempt.</summary>
    public string OperationKey { get; set; } = string.Empty;

    /// <summary>What the operation was for (the inbound message id), so a retry finds its earlier attempt.</summary>
    public string ReferenceId { get; set; } = string.Empty;

    public decimal CreditsBefore { get; set; }

    public decimal CreditsConsumed { get; set; }

    public decimal CreditsAfter { get; set; }

    /// <summary>Credits handed back because the platform or provider failed.</summary>
    public decimal CreditsRefunded { get; set; }

    public string? FailureReason { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
