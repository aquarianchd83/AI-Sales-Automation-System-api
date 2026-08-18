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
    /// named here without the "Intent" suffix since the model is prompted to return bare intent names.</summary>
    public string[] EscalationIntents { get; set; } = { "Complaint", "HumanRequest", "Negotiation", "ComplexTechnical" };

    /// <summary>Max knowledge base chunks retrieved as grounding context per AI turn.</summary>
    public int KnowledgeBaseTopN { get; set; } = 5;

    /// <summary>0.0-1.0. A retrieved chunk below this cosine similarity is dropped rather than passed
    /// to the AI as grounding - an irrelevant "closest available" chunk is worse than no chunk.</summary>
    public double MinRelevanceScore { get; set; } = 0.3;

    /// <summary>How many of the most recent messages in a conversation are included as history context
    /// for one AI turn. Bounds prompt size/cost; older context lives in Conversation.Summary instead.</summary>
    public int ConversationHistoryTurns { get; set; } = 10;
}
