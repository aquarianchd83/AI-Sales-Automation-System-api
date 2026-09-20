using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Stands in for a real LLM provider when none is configured (<c>AiProviders:Provider = "Simulated"</c>,
/// the default) - keyword/regex-based intent detection and canned replies, so the whole orchestrator
/// (auto-reply vs escalate, lead extraction, RAG grounding) is runnable and testable without any API
/// key, same role SimulatedWhatsAppClient plays for outbound sends.
/// </summary>
public class SimulatedAiClient : IAiService
{
    private static readonly string[] ComplaintKeywords = { "complain", "complaint", "angry", "terrible", "worst", "refund", "broken", "disappointed" };
    private static readonly string[] HumanRequestKeywords = { "human", "real person", "speak to someone", "agent please", "talk to a person" };
    private static readonly string[] NegotiationKeywords = { "discount", "lower price", "best price", "negotiate", "cheaper" };
    private static readonly string[] TechnicalKeywords = { "error code", "not working", "technical issue", "bug", "crash", "doesn't work" };
    private static readonly string[] PriceKeywords = { "price", "cost", "how much", "rate", "charges" };
    private static readonly string[] BuyingKeywords = { "want to buy", "how do i pay", "payment", "purchase", "book it", "ready to buy" };
    private static readonly string[] DemoKeywords = { "demo", "site visit", "appointment", "trial", "show me" };
    private static readonly string[] FaqKeywords = { "hours", "location", "hello", "hi", "info", "information" };

    private static readonly Regex BudgetPattern = new(@"(?:₹|rs\.?|inr|\$)\s?[\d,]+(?:k)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InterestPattern = new(@"interested in ([a-zA-Z0-9 ]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] TimelinePhrases = { "today", "tomorrow", "this week", "next week", "this month", "next month", "asap" };

    private readonly AiProviderSettings _settings;
    private readonly ILogger<SimulatedAiClient> _logger;
    private readonly Random _random = new();

    public SimulatedAiClient(IOptionsSnapshot<AiProviderSettings> settings, ILogger<SimulatedAiClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task<AiReplyResult> GetResponseAsync(AiConversationContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var text = context.InboundMessageText;

        if (_settings.SimulatedFailureRatePercent > 0 && _random.Next(100) < _settings.SimulatedFailureRatePercent)
        {
            _logger.LogWarning("[Simulated AI] Injected provider failure for conversation {ConversationId}", context.ConversationId);
            return Task.FromResult(AiClientFailure.Result("Simulated:rule-based", stopwatch.Elapsed, context.ExistingSummary));
        }

        var (intent, confidence) = DetectIntent(text);
        var extractedFields = ExtractFields(text, context.SchemaFields);
        var entities = ToLegacyEntities(extractedFields);
        var citedChunkIds = new List<Guid>();

        string responseText;
        if (intent == nameof(CustomerIntent.Information) && context.GroundingChunks.Count > 0)
        {
            var top = context.GroundingChunks[0];
            citedChunkIds.Add(top.ChunkId);
            responseText = $"Here's what I found: {Truncate(top.Text, 300)}";
        }
        else if (intent is nameof(CustomerIntent.Complaint) or nameof(CustomerIntent.HumanRequest)
                 or nameof(CustomerIntent.Negotiation) or nameof(CustomerIntent.Support))
        {
            responseText = "I understand, and I want to make sure this gets handled properly - connecting you with a team member who can help.";
        }
        else
        {
            responseText = "Thanks for reaching out! Someone from our team will follow up with more details shortly.";
        }

        var summary = BuildSummary(context.ExistingSummary, text);

        stopwatch.Stop();

        _logger.LogInformation(
            "[Simulated AI] conversation={ConversationId} intent={Intent} confidence={Confidence}",
            context.ConversationId, intent, confidence);

        return Task.FromResult(new AiReplyResult(
            responseText,
            intent,
            confidence,
            entities,
            summary,
            "Simulated:rule-based",
            PromptTokens: text.Length / 4,
            CompletionTokens: responseText.Length / 4,
            LatencyMs: (int)stopwatch.Elapsed.TotalMilliseconds,
            citedChunkIds,
            extractedFields,
            BuyingIntentDetected: intent is nameof(CustomerIntent.PurchaseIntent) or nameof(CustomerIntent.DemoRequest),
            HumanRequested: intent == nameof(CustomerIntent.HumanRequest),
            OptOutRequested: false,
            AskedFieldKey: context.FieldsToAsk.Count > 0 ? context.FieldsToAsk[0].FieldKey : null,
            DetectedLanguage: "en",
            AgentNote: $"Simulated turn; detected {intent}."));
    }

    /// <summary>Returns CustomerIntent names, the same closed set a real provider is now constrained
    /// to - a simulator emitting labels the enum does not contain would let configuration keyed on
    /// those intents pass in dev and silently do nothing in production.</summary>
    private static (string Intent, double Confidence) DetectIntent(string text)
    {
        if (ContainsAny(text, ComplaintKeywords)) return (nameof(CustomerIntent.Complaint), 0.9);
        if (ContainsAny(text, HumanRequestKeywords)) return (nameof(CustomerIntent.HumanRequest), 0.9);
        if (ContainsAny(text, BuyingKeywords)) return (nameof(CustomerIntent.PurchaseIntent), 0.9);
        if (ContainsAny(text, DemoKeywords)) return (nameof(CustomerIntent.DemoRequest), 0.9);
        if (ContainsAny(text, NegotiationKeywords)) return (nameof(CustomerIntent.Negotiation), 0.85);
        if (ContainsAny(text, TechnicalKeywords)) return (nameof(CustomerIntent.Support), 0.7);
        if (ContainsAny(text, PriceKeywords)) return (nameof(CustomerIntent.PriceEnquiry), 0.85);
        if (ContainsAny(text, FaqKeywords)) return (nameof(CustomerIntent.Information), 0.85);

        // Unrecognized phrasing - stay cautious rather than guess, so it escalates against the default
        // AiOptions.ConfidenceThreshold (0.6) instead of confidently auto-replying with a canned guess.
        return (nameof(CustomerIntent.Unknown), 0.5);
    }

    private static bool ContainsAny(string text, string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Produces the dynamic extraction shape, but only for keys the tenant's schema actually
    /// has - the simulator must not be able to return a field key a real provider could not, or the
    /// validator's "not in this schema" path would never be exercised in development.
    ///
    /// Only the three well-known keys have patterns; a tenant's custom fields are simply never
    /// extracted here, which is the honest limit of a regex stand-in for a language model.</summary>
    private static List<AiExtractedField> ExtractFields(string text, IReadOnlyList<AiQualificationField> schema)
    {
        var known = schema.Select(f => f.FieldKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<AiExtractedField>();

        var budgetMatch = BudgetPattern.Match(text);
        if (budgetMatch.Success && known.Contains(QualificationDefaults.BudgetKey))
            found.Add(new AiExtractedField(QualificationDefaults.BudgetKey, budgetMatch.Value, 0.8));

        var interestMatch = InterestPattern.Match(text);
        if (interestMatch.Success && known.Contains(QualificationDefaults.InterestKey))
            found.Add(new AiExtractedField(QualificationDefaults.InterestKey, interestMatch.Groups[1].Value.Trim(), 0.8));

        var timeline = TimelinePhrases.FirstOrDefault(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (timeline is not null && known.Contains(QualificationDefaults.PurchaseTimelineKey))
            found.Add(new AiExtractedField(QualificationDefaults.PurchaseTimelineKey, timeline, 0.8));

        return found;
    }

    private static AiExtractedEntities ToLegacyEntities(IReadOnlyList<AiExtractedField> fields) => new(
        Find(fields, QualificationDefaults.BudgetKey),
        Find(fields, QualificationDefaults.InterestKey),
        Find(fields, QualificationDefaults.PurchaseTimelineKey));

    private static string? Find(IReadOnlyList<AiExtractedField> fields, string key) =>
        fields.FirstOrDefault(f => string.Equals(f.FieldKey, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string BuildSummary(string? existingSummary, string latestMessage) =>
        string.IsNullOrWhiteSpace(existingSummary)
            ? $"Customer said: {Truncate(latestMessage, 200)}"
            : $"{existingSummary} Most recently: {Truncate(latestMessage, 200)}";

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
