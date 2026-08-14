namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// Delivered/Read arrive via WhatsApp status webhooks, which are Phase 4 scope - Phase 3 only ever
/// sets Queued, Sent or Failed, based on the synchronous send API response.
/// </summary>
public enum MessageStatus
{
    Queued = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}
