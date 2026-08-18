using System.Text;
using System.Text.Json.Serialization;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Shared prompt text and result-payload shape for the three real provider clients (Anthropic, OpenAI,
/// Google) - each wraps this in a different request/response envelope (Anthropic tool-use, OpenAI
/// function-calling, Google function declarations), but the instructions given to the model and the
/// structured fields asked back are identical, so only the plumbing differs per client.
/// </summary>
internal static class AiPromptSupport
{
    public const string ToolName = "record_response";

    public const string ToolDescription =
        "Record the reply to send and its classification. Always call this - never answer in plain text.";

    public static string SystemPrompt(string customerName) => $"""
        You are a WhatsApp sales assistant replying to {customerName} on behalf of a business.
        Answer ONLY using the knowledge base snippets provided in the user message - never invent
        product, pricing, or policy facts that are not in them. If the snippets do not cover the
        question, say so plainly and keep the reply short rather than guessing.

        After composing your reply, call {ToolName} with:
        - intent: one short label such as FAQ, Complaint, HumanRequest, Negotiation, ComplexTechnical,
          or General - whichever best fits, as a single word or short phrase.
        - confidence: 0.0-1.0, how confident you are that your reply correctly and fully answers the
          customer without a human needing to step in.
        - budget/interest/purchase_timeline: extract only if the customer actually stated them in this
          message or the recent history; omit or leave null otherwise - never fabricate a value.
        - response_text: the actual WhatsApp reply to send, in plain conversational text, no markdown.
        - updated_summary: a short 1-3 sentence running summary of the whole conversation so far,
          replacing the previous summary rather than appending to it.
        - cited_chunk_ids: the ids (from the snippets below, exactly as given) you actually used to
          answer, or an empty array if you did not use any.
        """;

    public static string BuildUserMessage(AiConversationContext context)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(context.ExistingSummary))
            sb.AppendLine($"Conversation summary so far: {context.ExistingSummary}").AppendLine();

        if (context.RecentHistory.Count > 0)
        {
            sb.AppendLine("Recent messages (oldest first):");
            foreach (var turn in context.RecentHistory)
                sb.AppendLine($"[{turn.Direction}] {turn.Text}");
            sb.AppendLine();
        }

        if (context.GroundingChunks.Count > 0)
        {
            sb.AppendLine("Knowledge base snippets:");
            foreach (var chunk in context.GroundingChunks)
                sb.AppendLine($"- id={chunk.ChunkId}: {chunk.Text}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("No knowledge base snippets were found for this message - answer only from general conversational ability, and say plainly if you cannot help without a human.").AppendLine();
        }

        sb.AppendLine($"Customer's new message: {context.InboundMessageText}");

        return sb.ToString();
    }

    /// <summary>Plain JSON Schema (object/string/number/array only, no provider-specific extensions),
    /// deliberately kept to the common subset all three providers accept as-is: Anthropic tool
    /// input_schema, OpenAI function parameters, and Google's OpenAPI-based function declaration
    /// schema. Optional fields are simply left out of "required" rather than given a nullable type
    /// union, since the dialects disagree on how to express that but agree on plain "type": "string".</summary>
    public static object ToolInputSchema() => new
    {
        type = "object",
        properties = new
        {
            intent = new { type = "string" },
            confidence = new { type = "number" },
            budget = new { type = "string" },
            interest = new { type = "string" },
            purchase_timeline = new { type = "string" },
            response_text = new { type = "string" },
            updated_summary = new { type = "string" },
            cited_chunk_ids = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "intent", "confidence", "response_text", "updated_summary", "cited_chunk_ids" }
    };

    public static AiReplyResult ToAiReplyResult(
        this ToolResultPayload payload, string modelUsed, int? promptTokens, int? completionTokens, TimeSpan elapsed, string? existingSummary)
    {
        var citedIds = (payload.CitedChunkIds ?? new List<string>())
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();

        return new AiReplyResult(
            payload.ResponseText ?? string.Empty,
            string.IsNullOrWhiteSpace(payload.Intent) ? "General" : payload.Intent,
            Math.Clamp(payload.Confidence, 0, 1),
            new AiExtractedEntities(
                string.IsNullOrWhiteSpace(payload.Budget) ? null : payload.Budget,
                string.IsNullOrWhiteSpace(payload.Interest) ? null : payload.Interest,
                string.IsNullOrWhiteSpace(payload.PurchaseTimeline) ? null : payload.PurchaseTimeline),
            string.IsNullOrWhiteSpace(payload.UpdatedSummary) ? (existingSummary ?? string.Empty) : payload.UpdatedSummary,
            modelUsed,
            promptTokens,
            completionTokens,
            (int)elapsed.TotalMilliseconds,
            citedIds);
    }
}

/// <summary>The structured fields every real provider is asked to return, whatever envelope
/// (tool-use/function-calling) it arrives wrapped in. Property names are snake_case to match what each
/// provider's tool/function schema actually asks the model to produce.</summary>
internal class ToolResultPayload
{
    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("budget")]
    public string? Budget { get; set; }

    [JsonPropertyName("interest")]
    public string? Interest { get; set; }

    [JsonPropertyName("purchase_timeline")]
    public string? PurchaseTimeline { get; set; }

    [JsonPropertyName("response_text")]
    public string? ResponseText { get; set; }

    [JsonPropertyName("updated_summary")]
    public string? UpdatedSummary { get; set; }

    [JsonPropertyName("cited_chunk_ids")]
    public List<string>? CitedChunkIds { get; set; }
}
