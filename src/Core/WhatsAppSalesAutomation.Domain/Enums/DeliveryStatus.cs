namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>How one channel of a <see cref="Entities.Billing.TenantNotification"/> went. Skipped means the
/// channel wasn't applicable (no address on file, or the tenant turned it off), which is not a failure.</summary>
public enum DeliveryStatus
{
    NotAttempted = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3
}
