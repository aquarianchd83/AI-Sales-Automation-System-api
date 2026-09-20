using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Generates a grounded reply plus structured extraction (intent, confidence, qualification values) for
/// one inbound customer message. Implemented in Infrastructure by an Anthropic Claude client, an OpenAI
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

/// <summary>
/// Everything the AI needs to answer one inbound message.
///
/// The three qualification lists are deliberately separate rather than one list with flags.
/// <see cref="SchemaFields"/> shapes the tool schema, because the customer may volunteer any field at
/// any moment and a field missing from the schema is one the model cannot report.
/// <see cref="KnownFields"/> and <see cref="FieldsToAsk"/> shape the prompt, and the split is what
/// makes "never ask what they already told you" structural: the model is only ever shown fields that
/// are still open, so re-asking an answered one is not an instruction it can forget - it is an option
/// it does not have.
/// </summary>
public record AiConversationContext(
    Guid ConversationId,
    string CustomerName,
    string InboundMessageText,
    IReadOnlyList<AiConversationTurn> RecentHistory,
    IReadOnlyList<AiKnowledgeSnippet> GroundingChunks,
    string? ExistingSummary,
    AiBusinessProfile Business,
    IReadOnlyList<AiQualificationField> SchemaFields,
    IReadOnlyList<AiCapturedField> KnownFields,
    IReadOnlyList<AiQualificationField> FieldsToAsk,
    /// <summary>True once the lead is hot. Suppresses the "still to learn" block entirely, so the
    /// agent stops qualifying and moves to the next step - prompt §11's rule, enforced by withholding
    /// the questions rather than by asking the model not to use them.</summary>
    bool QualificationPaused,
    /// <summary>The customer's saved language preference, if any. A hint rather than an instruction:
    /// the model is told to match whatever they actually wrote, because a stored preference goes stale
    /// and the message in front of it does not.</summary>
    string? PreferredLanguage);

/// <summary>The business the agent is speaking for. Every field but <see cref="Name"/> is optional, and
/// a blank one is omitted from the prompt rather than rendered as an empty label - "Location:" with
/// nothing after it spends tokens to tell the model nothing.</summary>
public record AiBusinessProfile(
    string Name,
    string? Industry,
    string? Location,
    string? Website,
    string? WorkingHours,
    string? ProductName,
    string? Description,
    ConversationGoal Goal,
    /// <summary>Whether the agent may say anything to the customer about how they are assessed.
    /// False adds an explicit prohibition to the system prompt.</summary>
    bool MayDiscloseLeadScore);

/// <summary>One configured qualification field, flattened for the prompt and the tool schema.</summary>
public record AiQualificationField(
    string FieldKey,
    string DisplayName,
    string? Description,
    string Question,
    QualificationDataType DataType,
    IReadOnlyList<string>? AllowedValues);

/// <summary>Something already known about this customer. Rendered so the model can see it and is told
/// not to ask about it.</summary>
public record AiCapturedField(string FieldKey, string DisplayName, string RawValue);

public record AiConversationTurn(MessageDirection Direction, string Text, DateTime Timestamp);

public record AiKnowledgeSnippet(Guid ChunkId, string Text, double RelevanceScore);

/// <summary>
/// <paramref name="ConfidenceScore"/> is 0.0-1.0, compared against <c>AiOptions.ConfidenceThreshold</c>
/// by the orchestrator to decide auto-reply vs escalate. <paramref name="CitedChunkIds"/> is the subset
/// of the context's GroundingChunks the model actually used, recorded as AiInteractionSource rows -
/// providers that cannot report which chunks they used should return all of GroundingChunks' ids rather
/// than an empty list, since an empty citation list would (incorrectly) read as "answered without any
/// grounding" in the audit trail.
///
/// The three boolean signals are the model's report, not a decision. Code decides what follows from
/// them - see <c>IAiReplyValidator</c> and the orchestrator's escalation gate.
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
    IReadOnlyList<Guid> CitedChunkIds,
    IReadOnlyList<AiExtractedField>? ExtractedFields = null,
    bool BuyingIntentDetected = false,
    bool HumanRequested = false,
    bool OptOutRequested = false,
    /// <summary>Which field the agent asked for in this turn, if any. Paired with what was actually
    /// captured, this is what shows whether asking works: a field asked three turns running and never
    /// captured is a badly phrased question, not a stubborn customer.</summary>
    string? AskedFieldKey = null,
    /// <summary>BCP-47 with a script subtag where it matters - "hi-Latn" for Roman Hinglish, "hi" for
    /// Devanagari, "en". The distinction is the point: replying in Devanagari to someone typing Roman
    /// Hinglish is a worse answer than replying in English.</summary>
    string? DetectedLanguage = null,
    /// <summary>At most one sentence of operational conclusion for the sales team. Not chain-of-thought:
    /// the model is asked for what it concluded, not how, and the value is truncated on write.</summary>
    string? AgentNote = null);

/// <summary>One qualification value the model claims the customer supplied. Claims, not facts - the
/// validator checks the key is in this tenant's schema and the value fits the field's type before any
/// of it is stored.</summary>
public record AiExtractedField(string FieldKey, string Value, double Confidence);

/// <summary>
/// The three well-known qualification values, kept as a distinct shape so every caller written against
/// it - <c>ILeadService.ApplyAiExtractedAttributesAsync</c>, <c>Lead.Budget</c>/<c>Interest</c>/
/// <c>PurchaseTimeline</c>, the lead list and its filters - keeps working unchanged while the UI moves
/// to dynamic fields.
///
/// Now a projection over <see cref="AiReplyResult.ExtractedFields"/> rather than the model's whole
/// output. A tenant whose schema no longer has these fields simply gets nulls, which is exactly what
/// those callers already handle.
/// </summary>
public record AiExtractedEntities(string? Budget, string? Interest, string? PurchaseTimeline);
