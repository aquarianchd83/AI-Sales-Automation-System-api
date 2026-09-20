namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "Ai" config section. Governs the AI/Human/Hybrid orchestrator's decisions - which
/// provider/model to use lives in Infrastructure's AiProviderSettings instead, since that involves API
/// keys the Application layer has no business holding (same split as MessagingOptions vs
/// WhatsAppSettings).
/// </summary>
public class AiOptions
{
    /// <summary>0.0-1.0. An AiInteraction with ConfidenceScore below this escalates to a HumanHandoff
    /// instead of auto-replying, regardless of DetectedIntent.</summary>
    public double ConfidenceThreshold { get; set; } = 0.6;

    /// <summary>Case-insensitive DetectedIntent values that always escalate even at high confidence -
    /// the Phase 1 design's ComplaintIntent/HumanRequestIntent/NegotiationIntent/ComplexTechnicalIntent,
    /// named here without the "Intent" suffix since the model is prompted to return bare intent names.
    /// Must default to an empty array, not the real 4-item list - ConfigurationBinder appends
    /// config-bound array items onto an already-non-null array property rather than replacing it, so a
    /// non-empty default here would come back from IOptionsSnapshot&lt;AiOptions&gt; with every intent
    /// duplicated. appsettings.json's own "Ai:EscalationIntents" array is what actually supplies the
    /// real default for a fresh install with no DB override.</summary>
    public string[] EscalationIntents { get; set; } = Array.Empty<string>();

    /// <summary>Max knowledge base chunks retrieved as grounding context per AI turn.</summary>
    public int KnowledgeBaseTopN { get; set; } = 5;

    /// <summary>0.0-1.0. A retrieved chunk below this cosine similarity is dropped rather than passed
    /// to the AI as grounding - an irrelevant "closest available" chunk is worse than no chunk.</summary>
    public double MinRelevanceScore { get; set; } = 0.3;

    /// <summary>How many of the most recent messages in a conversation are included as history context
    /// for one AI turn. Bounds prompt size/cost; older context lives in Conversation.Summary instead.</summary>
    public int ConversationHistoryTurns { get; set; } = 10;

    /// <summary>0.0-1.0. A qualification value the model extracted below this confidence is stored (so
    /// the audit trail keeps what it claimed) but does not count as known: it earns no score, and the
    /// agent still asks for that field properly. A half-understood budget in the CRM is worse than an
    /// unanswered one, because nobody knows to doubt it.
    ///
    /// Values a human entered, and values backfilled from the pre-configurable Lead.Budget/Interest/
    /// PurchaseTimeline columns, are recorded at 1.0 and so are never affected by this.</summary>
    public double MinFieldExtractionConfidence { get; set; } = 0.6;

    /// <summary>How many still-unknown qualification fields the agent is shown per turn. Three, not
    /// all of them: handing the model a list of twelve open fields is an invitation to work through
    /// them, and the order was already decided in code - the rest would only spend tokens. The model
    /// is separately told to ask at most one.</summary>
    public int MaxFieldsToAsk { get; set; } = 3;

    /// <summary>Whether a lead turning hot raises a handoff on its own. On by default: a customer who
    /// has said they are ready to buy is the one conversation a human most wants to take. A tenant
    /// that would rather let the agent close can turn it off.</summary>
    public bool HandoffOnHotLead { get; set; } = true;
}
