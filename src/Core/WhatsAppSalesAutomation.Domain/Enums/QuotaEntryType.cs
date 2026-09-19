namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What one row of the quota ledger records. The sign of the row's UnitsDelta always matches:
/// Allocation, Purchase, ConsumptionReversal and AdjustmentCredit add units; Consumption, Expiry,
/// RefundClawback and AdjustmentDebit remove them.</summary>
public enum QuotaEntryType
{
    Allocation = 0,
    Purchase = 1,
    Consumption = 2,
    ConsumptionReversal = 3,
    AdjustmentCredit = 4,
    AdjustmentDebit = 5,
    Expiry = 6,
    RefundClawback = 7,

    /// <summary>Units taken out of the wallet while a refund request waits for a decision.</summary>
    RefundHold = 8,

    /// <summary>Held units given back because the refund was rejected, cancelled, expired, or only partly approved.</summary>
    RefundRelease = 9
}
