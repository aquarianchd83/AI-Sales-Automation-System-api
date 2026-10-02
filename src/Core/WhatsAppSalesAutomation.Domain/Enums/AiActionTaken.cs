namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What the orchestrator did as a result of one AI turn - recorded on <c>AiInteraction</c> for
/// the AI-performance report (containment rate = Replied / total) and for audit.</summary>
public enum AiActionTaken
{
    /// <summary>AI generated a reply and it was sent back to the customer over WhatsApp.</summary>
    Replied = 0,

    /// <summary>Confidence too low, a sensitive intent, or a configured rule matched - a HumanHandoff
    /// was raised instead of auto-replying.</summary>
    Escalated = 1,

    /// <summary>The message needed no action at all (e.g. a delivery-receipt-only inbound artifact,
    /// or Mode == Human so the AI was never invoked). Rare; mostly a defensive catch-all.</summary>
    NoActionNeeded = 2,

    /// <summary>Hybrid mode only: the AI wrote a reply but did NOT send it. It is held as a draft for the
    /// assigned agent to send, edit or discard, and a handoff was raised. Counts as human-involved in
    /// the AI reports - the customer is waiting on a person, not on the model.</summary>
    Drafted = 3
}
