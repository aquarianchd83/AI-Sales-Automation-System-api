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
    RefundExpired = 7
}
