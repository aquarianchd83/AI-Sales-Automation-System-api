using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Conversations;

/// <summary>
/// One customer's message thread - the transcript both campaign sends (Phase 3) and inbound replies
/// (Phase 4) share, per <c>Message.ConversationId</c>. One active (non-Closed) conversation per
/// customer at a time; a closed one stays as history, and a new inbound message re-opens a fresh one
/// rather than reusing the closed thread.
///
/// Deliberately does not yet carry AiConfidenceLast/LastDetectedIntent/LastLeadScore/Summary from the
/// Phase 1 design - those are Phase 5 (AI/RAG) fields whose real shape depends on decisions not made
/// yet (embedding format, confidence scale, summarisation approach); adding speculative columns for
/// them now risks guessing wrong and needing to fix it anyway, unlike a plain enum value.
/// </summary>
public class Conversation : BaseEntity
{
    public Guid CustomerId { get; set; }

    public ConversationMode Mode { get; set; } = ConversationMode.AI;

    public ConversationStatus Status { get; set; } = ConversationStatus.Open;

    public Guid? AssignedAgentId { get; set; }

    /// <summary>Most recent message in either direction - drives inbox sort order.</summary>
    public DateTime? LastMessageAt { get; set; }

    /// <summary>Most recent *inbound* message specifically - the customer service window (free-form
    /// replies allowed for 24h after this, an approved template required otherwise) is measured from
    /// this, not LastMessageAt, since an outbound send must not extend a window the customer never
    /// re-opened.</summary>
    public DateTime? LastInboundMessageAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
