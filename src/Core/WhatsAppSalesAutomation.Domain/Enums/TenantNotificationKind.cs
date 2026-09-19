namespace WhatsAppSalesAutomation.Domain.Enums;

public enum TenantNotificationKind
{
    QuotaLow20 = 0,
    QuotaLow5 = 1,
    QuotaExhausted = 2,
    CreditsExpiring14 = 3,
    CreditsExpiring3 = 4,
    RefundApproved = 5,
    RefundRejected = 6,
    RefundExpired = 7,
    // The plan's billing period ends in a week / tomorrow: it renews (and is charged), or cannot renew.
    PlanExpiring7 = 8,
    PlanExpiring1 = 9
}
