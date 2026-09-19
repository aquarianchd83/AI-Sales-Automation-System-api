namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Where a bucket of units came from. The numeric order is the tie-break when two grants expire
/// at the same instant: included plan quota is drawn down before purchased credits.</summary>
public enum QuotaGrantOrigin
{
    PlanAllocation = 0,
    CreditPurchase = 1,
    Adjustment = 2,

    /// <summary>The small free allocation a new tenant gets for its trial.</summary>
    Trial = 3
}
