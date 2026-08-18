using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Ai;

/// <summary>One AI turn: the orchestrator's record of what it saw, what it decided, and what it did
/// for a single inbound message. Written whether the outcome was an auto-reply or an escalation - the
/// AI-performance report (confidence trends, escalation/containment rate) reads this table, not
/// HumanHandoffs, since not every low-confidence turn results in a handoff being created (Mode ==
/// Human bypasses the AI entirely and never writes a row here).</summary>
public class AiInteraction : BaseEntity
{
    public Guid ConversationId { get; set; }

    /// <summary>The inbound Message that triggered this AI turn.</summary>
    public Guid InboundMessageId { get; set; }

    public string? DetectedIntent { get; set; }

    /// <summary>0.0-1.0. Compared against <c>AiOptions.ConfidenceThreshold</c> to decide auto-reply vs escalate.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Structured extraction (budget/interest/timeline, etc.) as the provider returned it -
    /// kept as raw JSON rather than dedicated columns since the extracted attribute set is expected to
    /// evolve with prompt/provider changes; the Application layer's Lead service reads specific keys
    /// out of it when updating a Lead.</summary>
    public string? ExtractedEntitiesJson { get; set; }

    /// <summary>The reply text the AI generated, whether or not it was actually sent (escalated turns
    /// still keep the draft for the agent to see in the inbox).</summary>
    public string? ProposedResponseText { get; set; }

    public AiActionTaken ActionTaken { get; set; }

    /// <summary>Provider+model identifier, e.g. "Anthropic:claude-haiku-4-5" or "Simulated:rule-based" -
    /// a free-text snapshot rather than an FK/enum so changing providers never orphans historical rows.</summary>
    public string ModelUsed { get; set; } = string.Empty;

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int LatencyMs { get; set; }
}
