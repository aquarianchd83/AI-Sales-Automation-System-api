using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// <see cref="ILeadDiscoveryAgent"/> on Claude's Messages API (raw HttpClient, like
/// <see cref="AnthropicAiClient"/>), called with the platform's own key from
/// <see cref="LeadDiscoveryAgentSettings"/> rather than any tenant's - see that class for why discovery is
/// billed centrally. Claude researches with the server-side <c>web_search</c> and <c>web_fetch</c> tools and
/// reports through a strict client tool, <c>submit_leads</c>; the run ends as soon as that call arrives, with
/// no tool_result sent back.
///
/// The basic tool versions (<c>web_search_20250305</c>, <c>web_fetch_20250910</c>) are used on purpose: they
/// return every fetched page's text directly in the response, which is the evidence LeadQualification checks
/// phone numbers and emails against, and they work with any model the setting might name. The newer
/// dynamic-filtering versions run searches from inside code execution to trim what reaches the model, which
/// saves tokens but is not needed here.
///
/// Conversation handling follows the server-tool rules: a <c>pause_turn</c> response is sent back unchanged
/// so the server resumes; a turn that ends without <c>submit_leads</c> gets one explicit nudge; a refusal
/// fails the run. Claude Opus 5 / Fable 5 requests opt into server-side refusal fallbacks.
///
/// No request is logged with its body - the API key travels in a header, and HttpClient's default logging
/// only records URLs, which carry nothing sensitive here.
/// </summary>
public class AnthropicLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private const string FallbackBetaHeader = "server-side-fallback-2026-07-01";
    private const int MaxErrorBodyLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly LeadDiscoveryAgentSettings _settings;
    private readonly ILogger<AnthropicLeadDiscoveryAgent> _logger;

    public AnthropicLeadDiscoveryAgent(
        HttpClient httpClient,
        IOptions<LeadDiscoveryAgentSettings> settings,
        ILogger<AnthropicLeadDiscoveryAgent> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return LeadDiscoveryAgentResult.NotConfigured(
                "lead discovery has no Anthropic API key - set LeadDiscovery:Agent:ApiKey (environment variable LeadDiscovery__Agent__ApiKey).");
        }

        var evidence = new EvidenceCollector();
        var messages = new JsonArray { UserMessage(LeadDiscoveryPrompts.BuildUserMessage(request)) };
        var nudged = false;

        for (var turn = 1; turn <= _settings.MaxTurns; turn++)
        {
            var response = await SendAsync(messages, cancellationToken);
            var content = response["content"] as JsonArray ?? new JsonArray();
            var stopReason = Str(response["stop_reason"]);

            evidence.Collect(content, response["usage"]);

            if (stopReason == "refusal")
            {
                var category = response["stop_details"] is JsonObject details ? Str(details["category"]) : null;
                throw new InvalidOperationException($"Claude declined the lead discovery request (category: {category ?? "none given"}).");
            }

            var submission = content.OfType<JsonObject>().FirstOrDefault(block =>
                Str(block["type"]) == "tool_use" && Str(block["name"]) == LeadDiscoveryPrompts.SubmitToolName);

            if (submission is not null)
                return evidence.ToResult(ParseCandidates(submission["input"]), _settings.Model);

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

            // The server-side tool loop hit its iteration limit; sending the turn back resumes it.
            if (stopReason == "pause_turn")
                continue;

            if (nudged)
                break;

            nudged = true;
            messages.Add(UserMessage(LeadDiscoveryPrompts.SubmitNudge));
        }

        _logger.LogWarning("Lead discovery round ended without a {ToolName} call after {MaxTurns} turns", LeadDiscoveryPrompts.SubmitToolName, _settings.MaxTurns);
        return evidence.ToResult(Array.Empty<DiscoveredBusinessCandidate>(), _settings.Model);
    }

    private async Task<JsonObject> SendAsync(JsonArray messages, CancellationToken cancellationToken)
    {
        var useFallbacks = SupportsDefaultFallbacks(_settings.Model);

        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["max_tokens"] = _settings.MaxTokens,
            // Automatic prompt caching: pause_turn continuations and the submit nudge resend the whole
            // conversation so far, which is exactly the prefix this lets the API reuse.
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            ["system"] = LeadDiscoveryPrompts.SystemPrompt,
            ["messages"] = messages.DeepClone(),
            ["tools"] = BuildTools(),
            ["tool_choice"] = new JsonObject { ["type"] = "auto" }
        };

        if (!string.IsNullOrWhiteSpace(_settings.Effort))
            payload["output_config"] = new JsonObject { ["effort"] = _settings.Effort };

        if (useFallbacks)
            payload["fallbacks"] = "default";

        var uri = new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(_settings.BaseUrl)), "messages");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("x-api-key", _settings.ApiKey);
        httpRequest.Headers.Add("anthropic-version", _settings.ApiVersion);
        if (useFallbacks)
            httpRequest.Headers.Add("anthropic-beta", FallbackBetaHeader);

        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            // Thrown rather than swallowed so the run is recorded as Failed with Anthropic's own reason (an
            // invalid key, exhausted credit, web search disabled for the organization).
            var detail = body.Length > MaxErrorBodyLength ? body[..MaxErrorBodyLength] : body;
            throw new HttpRequestException($"Anthropic lead discovery request failed ({(int)httpResponse.StatusCode}): {detail}", null, httpResponse.StatusCode);
        }

        return JsonNode.Parse(body) as JsonObject
               ?? throw new JsonException("Anthropic returned a response that is not a JSON object.");
    }

    private JsonArray BuildTools() => new()
    {
        new JsonObject
        {
            ["type"] = "web_search_20250305",
            ["name"] = "web_search",
            ["max_uses"] = _settings.MaxSearchesPerRound
        },
        new JsonObject
        {
            ["type"] = "web_fetch_20250910",
            ["name"] = "web_fetch",
            ["max_uses"] = _settings.MaxFetchesPerRound,
            ["max_content_tokens"] = _settings.MaxFetchContentTokens
        },
        new JsonObject
        {
            ["name"] = LeadDiscoveryPrompts.SubmitToolName,
            ["description"] = LeadDiscoveryPrompts.SubmitToolDescription,
            ["strict"] = true,
            ["input_schema"] = JsonNode.Parse(LeadDiscoveryPrompts.SubmitToolInputSchema)
        }
    };

    /// <summary>The <c>fallbacks: "default"</c> mode is offered for Claude Opus 5 and the Fable 5 family;
    /// other models are sent without it.</summary>
    private static bool SupportsDefaultFallbacks(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("claude-fable-5", StringComparison.OrdinalIgnoreCase);

    private static JsonObject UserMessage(string text) => new() { ["role"] = "user", ["content"] = text };

    private static IReadOnlyList<DiscoveredBusinessCandidate> ParseCandidates(JsonNode? input)
    {
        var submission = input?.Deserialize<SubmitLeadsInput>(JsonOptions);

        return (submission?.Candidates ?? new List<CandidatePayload>())
            .Select(c => new DiscoveredBusinessCandidate(
                c.BusinessName ?? string.Empty,
                c.BusinessType ?? string.Empty,
                c.ContactPerson,
                c.Address,
                c.City,
                c.State,
                c.Phone,
                c.PhoneSourceUrl,
                c.Email,
                c.Website,
                c.SourceUrl ?? string.Empty,
                c.IsIndependentBusiness,
                c.IsPermanentlyClosed,
                c.LeadScore,
                c.ScoreRationale))
            .ToList();
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    /// <summary>Accumulates what a round actually looked at, across every response in it.</summary>
    private sealed class EvidenceCollector
    {
        private readonly HashSet<string> _seenUrls = new(StringComparer.Ordinal);
        private readonly List<FetchedPage> _pages = new();
        private LeadDiscoveryUsage _usage = LeadDiscoveryUsage.None;

        public void Collect(JsonArray content, JsonNode? usage)
        {
            foreach (var block in content.OfType<JsonObject>())
            {
                switch (Str(block["type"]))
                {
                    // A list of results on success, a single error object on failure.
                    case "web_search_tool_result" when block["content"] is JsonArray results:
                        foreach (var result in results.OfType<JsonObject>())
                            AddSeen(Str(result["url"]));
                        break;

                    case "web_fetch_tool_result" when block["content"] is JsonObject fetch && Str(fetch["type"]) == "web_fetch_result":
                        var url = Str(fetch["url"]);
                        AddSeen(url);

                        // PDFs come back base64-encoded and are not searched for contact details.
                        if (url is not null
                            && fetch["content"] is JsonObject document
                            && document["source"] is JsonObject source
                            && Str(source["type"]) == "text"
                            && Str(source["data"]) is { } text)
                        {
                            _pages.Add(new FetchedPage(url, text));
                        }
                        break;

                    case "text" when block["citations"] is JsonArray citations:
                        foreach (var citation in citations.OfType<JsonObject>())
                            AddSeen(Str(citation["url"]));
                        break;
                }
            }

            if (usage is JsonObject usageObject)
            {
                var serverToolUse = usageObject["server_tool_use"] as JsonObject;

                _usage += new LeadDiscoveryUsage(
                    Int(usageObject["input_tokens"]),
                    Int(usageObject["output_tokens"]),
                    Int(usageObject["cache_read_input_tokens"]),
                    Int(usageObject["cache_creation_input_tokens"]),
                    Int(serverToolUse?["web_search_requests"]),
                    Int(serverToolUse?["web_fetch_requests"]));
            }
        }

        public LeadDiscoveryAgentResult ToResult(IReadOnlyList<DiscoveredBusinessCandidate> candidates, string model) =>
            new(true, null, candidates, new LeadDiscoveryEvidence(_seenUrls.ToList(), _pages.ToList()), _usage, model);

        private void AddSeen(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url))
                _seenUrls.Add(url);
        }
    }

    private sealed class SubmitLeadsInput
    {
        public List<CandidatePayload>? Candidates { get; set; }
    }

    private sealed class CandidatePayload
    {
        public string? BusinessName { get; set; }
        public string? BusinessType { get; set; }
        public string? ContactPerson { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Phone { get; set; }
        public string? PhoneSourceUrl { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
        public string? SourceUrl { get; set; }
        public bool? IsIndependentBusiness { get; set; }
        public bool? IsPermanentlyClosed { get; set; }
        public int LeadScore { get; set; }
        public string? ScoreRationale { get; set; }
    }
}
