namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>
/// Implements the Phase 1 design's AI/Human/Hybrid state machine (architecture doc &sect;8). Called by
/// InboundWebhookProcessor once a non-opt-out inbound message has been persisted - deciding what
/// happens next (auto-reply, escalate, or defer entirely to a human) is orchestration logic that
/// belongs in the Application layer, not inline in the webhook processor.
/// </summary>
public interface IConversationOrchestrator
{
    Task HandleInboundMessageAsync(Guid conversationId, Guid customerId, Guid inboundMessageId, CancellationToken cancellationToken = default);
}
