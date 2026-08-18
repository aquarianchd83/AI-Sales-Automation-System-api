using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Conversations;

/// <summary>
/// One customer's message thread - the transcript both campaign sends (Phase 3) and inbound replies
/// (Phase 4) share, per <c>Message.ConversationId</c>. One active (non-Closed) conversation per
/// customer at a time; a closed one stays as history, and a new inbound message re-opens a fresh one
/// rather than reusing the closed thread.
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

    /// <summary>Confidence score (0.0-1.0) from the most recent AiInteraction on this conversation -
    /// a quick-glance signal for the inbox list, not a substitute for reading the AiInteraction rows.</summary>
    public double? AiConfidenceLast { get; set; }

    public string? LastDetectedIntent { get; set; }

    /// <summary>Denormalized copy of this conversation's Lead.Score, kept in sync by LeadService so the
    /// inbox list can sort/filter by lead temperature without a join for every row.</summary>
    public LeadScoreBand? LastLeadScore { get; set; }

    /// <summary>AI-maintained running summary of the conversation so far, regenerated (not appended)
    /// after each AI turn - gives an agent picking up a handoff the gist without reading the full
    /// transcript.</summary>
    public string? Summary { get; set; }
}
