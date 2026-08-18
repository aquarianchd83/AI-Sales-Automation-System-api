using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Conversations;

/// <summary>An escalation raised against a conversation, queued for a human agent to claim and resolve.</summary>
public class HumanHandoff : BaseEntity
{
    public Guid ConversationId { get; set; }

    public HandoffTriggerReason TriggerReason { get; set; }

    public HandoffStatus Status { get; set; } = HandoffStatus.Pending;

    public Guid? AssignedAgentId { get; set; }

    public DateTime? AssignedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public string? Notes { get; set; }
}
