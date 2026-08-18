using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Generates a grounded reply plus structured extraction (intent, confidence, lead attributes) for one
/// inbound customer message. Implemented in Infrastructure by an Anthropic Claude client, an OpenAI
/// client, a Google Gemini client, and a Simulated (rule-based, no API key needed) client, selected via
/// <c>Ai:Provider</c> config - the Application layer (ConversationOrchestrator) never knows which is
/// running, per the same provider-abstraction principle as <see cref="IWhatsAppService"/>.
///
/// One call handles both jobs (reply generation and structured extraction) rather than two round-trips,
/// since all three real providers support returning both the reply text and structured fields from a
/// single request (tool-use / function-calling / JSON mode) - splitting it into two calls would double
/// latency and cost for no benefit.
/// </summary>
public interface IAiService
{
    Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default);
}

/// <summary>Everything the AI needs to answer one inbound message: the message itself, enough recent
/// history to follow the thread, and the RAG-retrieved knowledge base snippets it is allowed to ground
/// its answer in - the model is instructed to answer only from <see cref="GroundingChunks"/> plus
/// general conversational ability, never to invent product/policy facts.</summary>
public record AiConversationContext(
    Guid ConversationId,
    string CustomerName,
    string InboundMessageText,
    IReadOnlyList<AiConversationTurn> RecentHistory,
    IReadOnlyList<AiKnowledgeSnippet> GroundingChunks,
    string? ExistingSummary);

public record AiConversationTurn(MessageDirection Direction, string Text, DateTime Timestamp);

public record AiKnowledgeSnippet(Guid ChunkId, string Text, double RelevanceScore);

/// <summary>
/// <paramref name="ConfidenceScore"/> is 0.0-1.0, compared against <c>AiOptions.ConfidenceThreshold</c>
/// by the orchestrator to decide auto-reply vs escalate. <paramref name="CitedChunkIds"/> is the subset
/// of the context's GroundingChunks the model actually used, recorded as AiInteractionSource rows -
/// providers that cannot report which chunks they used should return all of GroundingChunks' ids rather
/// than an empty list, since an empty citation list would (incorrectly) read as "answered without any
/// grounding" in the audit trail.
/// </summary>
public record AiReplyResult(
    string ResponseText,
    string DetectedIntent,
    double ConfidenceScore,
    AiExtractedEntities ExtractedEntities,
    string UpdatedSummary,
    string ModelUsed,
    int? PromptTokens,
    int? CompletionTokens,
    int LatencyMs,
    IReadOnlyList<Guid> CitedChunkIds);

/// <summary>Structured lead-qualification signal extracted from the conversation so far. All fields
/// are free text (not enums) since the model's phrasing of e.g. a budget range is exactly the kind of
/// thing that should not be forced into a rigid shape this early - LeadService is what turns this into
/// the more structured Lead.ScoreNumeric.</summary>
public record AiExtractedEntities(string? Budget, string? Interest, string? PurchaseTimeline);
