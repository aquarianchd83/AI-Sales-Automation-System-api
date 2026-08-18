using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Shared "the provider call failed" result construction for every IAiService implementation.
/// Returned instead of throwing, so a provider outage reads to ConversationOrchestrator as zero
/// confidence (always below AiOptions.ConfidenceThreshold) and escalates to a HumanHandoff, rather than
/// the exception propagating up and only being recorded as a generic WebhookEvent.Failed with no
/// automatic retry - see InboundWebhookProcessor.ProcessAsync's own doc comment on why that blanket
/// catch exists for genuinely unrecoverable payload problems, which a transient provider outage is not:
/// this way the customer still gets a human, and the WebhookEvent still ends as Processed.
/// </summary>
internal static class AiClientFailure
{
    public static AiReplyResult Result(string modelUsed, TimeSpan elapsed, string? existingSummary) => new(
        ResponseText: string.Empty,
        DetectedIntent: "ProviderError",
        ConfidenceScore: 0,
        ExtractedEntities: new AiExtractedEntities(null, null, null),
        UpdatedSummary: existingSummary ?? string.Empty,
        ModelUsed: modelUsed,
        PromptTokens: null,
        CompletionTokens: null,
        LatencyMs: (int)elapsed.TotalMilliseconds,
        CitedChunkIds: Array.Empty<Guid>());
}
