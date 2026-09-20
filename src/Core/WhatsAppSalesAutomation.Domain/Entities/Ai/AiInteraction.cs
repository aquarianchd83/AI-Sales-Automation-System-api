using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Ai;

/// <summary>One AI turn: the orchestrator's record of what it saw, what it decided, and what it did
/// for a single inbound message. Written whether the outcome was an auto-reply or an escalation - the
/// AI-performance report (confidence trends, escalation/containment rate) reads this table, not
/// HumanHandoffs, since not every low-confidence turn results in a handoff being created (Mode ==
/// Human bypasses the AI entirely and never writes a row here).</summary>
public class AiInteraction : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>The inbound Message that triggered this AI turn.</summary>
    public Guid InboundMessageId { get; set; }

    public string? DetectedIntent { get; set; }

    /// <summary>0.0-1.0. Compared against <c>AiOptions.ConfidenceThreshold</c> to decide auto-reply vs escalate.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>The qualification values this turn ACCEPTED, as raw JSON - not what the provider
    /// claimed. A claim about a field the tenant does not have, or a value its type rejects, is
    /// dropped before it reaches here, so this is a record of what was believed rather than what was
    /// asserted. Raw JSON rather than dedicated columns because the field set is per-tenant now and
    /// changes without a migration.</summary>
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

    /// <summary>The qualification FieldKeys this turn actually captured, as a JSON array. Lets the AI
    /// performance report answer "how many turns does qualification really take" without joining
    /// through LeadQualificationValue.</summary>
    public string? CapturedFieldKeysJson { get; set; }

    /// <summary>The field the agent asked for in this turn, if any. Paired with
    /// <see cref="CapturedFieldKeysJson"/> this is what shows whether asking works: a field asked
    /// three turns running and never captured is a badly phrased question, not a stubborn customer.</summary>
    public string? AskedFieldKey { get; set; }

    /// <summary>What the model reported, before code decided anything. Kept separate from
    /// <see cref="ActionTaken"/> so an audit can tell "the model said buying intent and we agreed"
    /// from "the model said it and our rules overrode it".</summary>
    public bool BuyingIntentReported { get; set; }

    public bool HumanRequestReported { get; set; }

    public bool OptOutReported { get; set; }
}
