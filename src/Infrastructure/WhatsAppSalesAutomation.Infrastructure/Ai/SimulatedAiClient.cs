using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

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
    private static readonly string[] FaqKeywords = { "price", "cost", "how much", "hours", "location", "hello", "hi", "info", "information" };

    private static readonly Regex BudgetPattern = new(@"(?:₹|rs\.?|inr|\$)\s?[\d,]+(?:k)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InterestPattern = new(@"interested in ([a-zA-Z0-9 ]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] TimelinePhrases = { "today", "tomorrow", "this week", "next week", "this month", "next month", "asap" };

    private readonly AiProviderSettings _settings;
    private readonly ILogger<SimulatedAiClient> _logger;
    private readonly Random _random = new();

    public SimulatedAiClient(IOptions<AiProviderSettings> settings, ILogger<SimulatedAiClient> logger)
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
        var entities = ExtractEntities(text);
        var citedChunkIds = new List<Guid>();

        string responseText;
        if (intent == "FAQ" && context.GroundingChunks.Count > 0)
        {
            var top = context.GroundingChunks[0];
            citedChunkIds.Add(top.ChunkId);
            responseText = $"Here's what I found: {Truncate(top.Text, 300)}";
        }
        else if (intent is "Complaint" or "HumanRequest" or "Negotiation" or "ComplexTechnical")
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
            citedChunkIds));
    }

    private static (string Intent, double Confidence) DetectIntent(string text)
    {
        if (ContainsAny(text, ComplaintKeywords)) return ("Complaint", 0.9);
        if (ContainsAny(text, HumanRequestKeywords)) return ("HumanRequest", 0.9);
        if (ContainsAny(text, NegotiationKeywords)) return ("Negotiation", 0.85);
        if (ContainsAny(text, TechnicalKeywords)) return ("ComplexTechnical", 0.7);
        if (ContainsAny(text, FaqKeywords)) return ("FAQ", 0.85);

        // Unrecognized phrasing - stay cautious rather than guess, so it escalates against the default
        // AiOptions.ConfidenceThreshold (0.6) instead of confidently auto-replying with a canned guess.
        return ("General", 0.5);
    }

    private static bool ContainsAny(string text, string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static AiExtractedEntities ExtractEntities(string text)
    {
        var budgetMatch = BudgetPattern.Match(text);
        var interestMatch = InterestPattern.Match(text);
        var timeline = TimelinePhrases.FirstOrDefault(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        return new AiExtractedEntities(
            budgetMatch.Success ? budgetMatch.Value : null,
            interestMatch.Success ? interestMatch.Groups[1].Value.Trim() : null,
            timeline);
    }

    private static string BuildSummary(string? existingSummary, string latestMessage) =>
        string.IsNullOrWhiteSpace(existingSummary)
            ? $"Customer said: {Truncate(latestMessage, 200)}"
            : $"{existingSummary} Most recently: {Truncate(latestMessage, 200)}";

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
