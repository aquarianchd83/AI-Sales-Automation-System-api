namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// AI defaults per the Phase 1 design, but no <c>IAiService</c> exists until Phase 5 - until then,
/// every inbound message escalates to a human regardless of Mode (see InboundWebhookProcessor), so
/// Mode is recorded for forward-compatibility rather than acted on differently per value yet.
/// </summary>
public enum ConversationMode
{
    AI = 0,
    Human = 1,
    Hybrid = 2
}
