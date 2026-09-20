using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Conversations;

/// <summary>An escalation raised against a conversation, queued for a human agent to claim and resolve.</summary>
public class HumanHandoff : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ConversationId { get; set; }

    public HandoffTriggerReason TriggerReason { get; set; }

    public HandoffStatus Status { get; set; } = HandoffStatus.Pending;

    public Guid? AssignedAgentId { get; set; }

    public DateTime? AssignedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public string? Notes { get; set; }

    /// <summary>The structured briefing for whoever picks this up: requirement, captured
    /// qualification, what is still unknown, score with its breakdown, intent, and why the agent
    /// stepped back. Serialized at handoff time rather than rendered on view, because it records what
    /// the agent knew at that moment - the lead keeps changing afterwards, and a regenerated summary
    /// would quietly describe a different situation than the one that triggered the handoff.</summary>
    public string? SummaryJson { get; set; }
}
