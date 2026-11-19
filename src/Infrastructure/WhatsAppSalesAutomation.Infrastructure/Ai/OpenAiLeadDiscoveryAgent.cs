using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// <see cref="ILeadDiscoveryAgent"/> on OpenAI's Responses API (ChatGPT models), the counterpart of
/// <see cref="AnthropicLeadDiscoveryAgent"/>. The model researches with OpenAI's hosted <c>web_search</c> tool,
/// reads contact pages with a client-side <c>fetch_page</c> function (OpenAI has no hosted page fetcher, and
/// LeadQualification needs the page text to verify phone numbers and emails - see
/// <see cref="LeadDiscoveryPageFetcher"/>), and reports through a strict <c>submit_leads</c> function; the run
/// ends as soon as that call arrives.
///
/// The conversation is carried with <c>previous_response_id</c>, so each turn sends only the new items (the
/// fetch results, or the nudge). A turn that ends without calling anything gets one explicit nudge to submit.
///
/// The key is the OpenAI key saved under Configuration > AI Providers unless a dedicated one is set in
/// <see cref="LeadDiscoveryAgentSettings"/>; the On/Off switch for OpenAI there covers discovery too.
/// </summary>
public class OpenAiLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private const int MaxErrorBodyLength = 500;

    private readonly HttpClient _httpClient;
    private readonly LeadDiscoveryAgentSettings _settings;
    private readonly ILogger<OpenAiLeadDiscoveryAgent> _logger;
    private readonly IPlatformAiConfigProvider _aiConfig;
    private readonly LeadDiscoveryPageFetcher _fetcher;

    public OpenAiLeadDiscoveryAgent(
        HttpClient httpClient,
        IOptionsSnapshot<LeadDiscoveryAgentSettings> settings,
        ILogger<OpenAiLeadDiscoveryAgent> logger,
        IPlatformAiConfigProvider aiConfig,
        LeadDiscoveryPageFetcher fetcher)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
        _aiConfig = aiConfig;
        _fetcher = fetcher;
    }

    public async Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        var config = _aiConfig.Get();

        if (!config.OpenAiEnabled)
            return LeadDiscoveryAgentResult.NotConfigured("OpenAI (ChatGPT) is turned off under Configuration > AI Providers, so lead discovery cannot run.");

        var apiKey = Blank(_settings.OpenAiApiKey) ?? config.OpenAiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return LeadDiscoveryAgentResult.NotConfigured(
                "lead discovery has no OpenAI API key - save one under Configuration > AI Providers (OpenAI), or set LeadDiscovery:Agent:OpenAiApiKey (environment variable LeadDiscovery__Agent__OpenAiApiKey).");
        }

        var model = Blank(_settings.OpenAiModel) ?? "gpt-5-mini";
        var baseUrl = Blank(_settings.OpenAiBaseUrl) ?? config.OpenAiBaseUrl;

        var evidence = new EvidenceCollector();
        var input = new JsonArray { UserMessage(LeadDiscoveryPrompts.BuildUserMessage(request)) };
        string? previousResponseId = null;
        var nudged = false;

        for (var turn = 1; turn <= _settings.MaxTurns; turn++)
        {
            var response = await SendAsync(apiKey, baseUrl, model, input, previousResponseId, cancellationToken);
            previousResponseId = Str(response["id"]);
            var output = response["output"] as JsonArray ?? new JsonArray();

            evidence.Collect(output, response["usage"]);
            ThrowIfFailed(response, output);

            var calls = output.OfType<JsonObject>().Where(item => Str(item["type"]) == "function_call").ToList();

            var submission = calls.FirstOrDefault(call => Str(call["name"]) == LeadDiscoveryPrompts.SubmitToolName);
            if (submission is not null)
            {
                var submitted = ParseArguments(submission["arguments"]);
                var candidates = LeadSubmission.Parse(submitted);
                LeadSubmission.LogOutcome(_logger, "OpenAI", candidates.Count, request.MaxCandidates, submitted);
                return evidence.ToResult(candidates, model, LeadSubmission.NoteOf(submitted));
            }

            // Answers to the fetch_page calls; the next turn continues from the previous response with just these.
            var answers = await Task.WhenAll(calls.Select(call => AnswerAsync(call, evidence, cancellationToken)));
            var next = new JsonArray();
            foreach (var answer in answers.OfType<JsonObject>())
                next.Add(answer);

            if (next.Count > 0)
            {
                input = next;
                continue;
            }

            if (previousResponseId is null || nudged)
                break;

            nudged = true;
            input = new JsonArray { UserMessage(LeadDiscoveryPrompts.SubmitNudge) };
        }

        _logger.LogWarning("OpenAI lead discovery round ended without a {ToolName} call after {MaxTurns} turns", LeadDiscoveryPrompts.SubmitToolName, _settings.MaxTurns);
        return evidence.ToResult(Array.Empty<DiscoveredBusinessCandidate>(), model, LeadSubmission.NoSubmissionNote);
    }

    /// <summary>The function_call_output for one call, or null when the call has no id to answer.</summary>
    private async Task<JsonObject?> AnswerAsync(JsonObject call, EvidenceCollector evidence, CancellationToken cancellationToken)
    {
        var callId = Str(call["call_id"]);
        if (callId is null)
            return null;

        string output;
        if (Str(call["name"]) != LeadDiscoveryPrompts.FetchToolName)
        {
            output = "error: unknown tool";
        }
        else if (ParseArguments(call["arguments"]) is not JsonObject arguments || Str(arguments["url"]) is not { } url)
        {
            output = "error: fetch_page needs a url";
        }
        else if (!evidence.TryReserveFetch(_settings.MaxFetchesPerRound))
        {
            output = "error: the page fetch limit for this round is reached. Call submit_leads now with what you have verified.";
        }
        else
        {
            var page = await _fetcher.FetchAsync(url, _settings.OpenAiMaxFetchChars, cancellationToken);
            if (page.Ok && page.Text is not null)
            {
                evidence.AddPage(url, page.Text);
                output = page.Text;
            }
            else
            {
                output = $"error: could not read {url} ({page.Error})";
            }
        }

        return new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output };
    }

    private async Task<JsonObject> SendAsync(string apiKey, string baseUrl, string model, JsonArray input, string? previousResponseId, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = LeadDiscoveryPrompts.OpenAiSystemPrompt,
            ["input"] = input.DeepClone(),
            ["tools"] = BuildTools(),
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["max_output_tokens"] = _settings.MaxTokens,
            ["max_tool_calls"] = _settings.MaxSearchesPerRound,
            ["include"] = new JsonArray { "web_search_call.action.sources" }
        };

        if (previousResponseId is not null)
            payload["previous_response_id"] = previousResponseId;

        if (!string.IsNullOrWhiteSpace(_settings.OpenAiReasoningEffort))
            payload["reasoning"] = new JsonObject { ["effort"] = _settings.OpenAiReasoningEffort };

        var uri = new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(baseUrl)), "responses");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            // Thrown rather than swallowed so the run is recorded as Failed with OpenAI's own reason (an invalid
            // key, no credit, a model without web search).
            var detail = body.Length > MaxErrorBodyLength ? body[..MaxErrorBodyLength] : body;
            throw new HttpRequestException($"OpenAI lead discovery request failed ({(int)httpResponse.StatusCode}): {detail}", null, httpResponse.StatusCode);
        }

        return JsonNode.Parse(body) as JsonObject
               ?? throw new System.Text.Json.JsonException("OpenAI returned a response that is not a JSON object.");
    }

    private JsonArray BuildTools() => new()
    {
        new JsonObject { ["type"] = "web_search" },
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = LeadDiscoveryPrompts.FetchToolName,
            ["description"] = LeadDiscoveryPrompts.FetchToolDescription,
            ["strict"] = true,
            ["parameters"] = JsonNode.Parse(LeadDiscoveryPrompts.FetchToolInputSchema)
        },
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = LeadDiscoveryPrompts.SubmitToolName,
            ["description"] = LeadDiscoveryPrompts.SubmitToolDescription,
            ["strict"] = true,
            ["parameters"] = JsonNode.Parse(LeadDiscoveryPrompts.SubmitToolInputSchema)
        }
    };

    private static void ThrowIfFailed(JsonObject response, JsonArray output)
    {
        if (response["error"] is JsonObject error)
            throw new InvalidOperationException($"OpenAI lead discovery failed: {Str(error["message"]) ?? "no reason given"}");

        if (Str(response["status"]) == "failed")
            throw new InvalidOperationException("OpenAI lead discovery failed without giving a reason.");

        var refused = output.OfType<JsonObject>()
            .Where(item => Str(item["type"]) == "message")
            .SelectMany(item => (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .FirstOrDefault(part => Str(part["type"]) == "refusal");

        if (refused is not null)
            throw new InvalidOperationException($"OpenAI declined the lead discovery request: {Str(refused["refusal"]) ?? "no reason given"}");
    }

    /// <summary>A function call's arguments arrive as a JSON string.</summary>
    private static JsonNode? ParseArguments(JsonNode? arguments)
    {
        if (arguments is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return arguments;
    }

    private static JsonObject UserMessage(string text) => new() { ["role"] = "user", ["content"] = text };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    /// <summary>Accumulates what a round actually looked at, across every response in it.</summary>
    private sealed class EvidenceCollector
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _seenUrls = new(StringComparer.Ordinal);
        private readonly List<FetchedPage> _pages = new();
        private LeadDiscoveryUsage _usage = LeadDiscoveryUsage.None;
        private int _fetches;

        public void Collect(JsonArray output, JsonNode? usage)
        {
            var searches = 0;

            foreach (var item in output.OfType<JsonObject>())
            {
                switch (Str(item["type"]))
                {
                    case "web_search_call":
                        searches++;
                        if (item["action"] is JsonObject action && action["sources"] is JsonArray sources)
                        {
                            foreach (var source in sources.OfType<JsonObject>())
                                AddSeen(Str(source["url"]));
                        }
                        break;

                    case "message":
                        foreach (var part in (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            foreach (var annotation in (part["annotations"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            {
                                if (Str(annotation["type"]) == "url_citation")
                                    AddSeen(Str(annotation["url"]));
                            }
                        }
                        break;
                }
            }

            var inputTokens = 0;
            var cached = 0;
            var outputTokens = 0;
            if (usage is JsonObject usageObject)
            {
                inputTokens = Int(usageObject["input_tokens"]);
                cached = Int((usageObject["input_tokens_details"] as JsonObject)?["cached_tokens"]);
                outputTokens = Int(usageObject["output_tokens"]);
            }

            // OpenAI counts cached tokens inside input_tokens; the usage record keeps them apart.
            _usage += new LeadDiscoveryUsage(Math.Max(0, inputTokens - cached), outputTokens, cached, 0, searches, 0);
        }

        /// <summary>Counts a fetch against the round's limit; false when the limit is already used up.</summary>
        public bool TryReserveFetch(int max)
        {
            lock (_gate)
            {
                if (_fetches >= max)
                    return false;
                _fetches++;
                return true;
            }
        }

        public void AddPage(string url, string text)
        {
            lock (_gate)
            {
                AddSeen(url);
                _pages.Add(new FetchedPage(url, text));
            }
        }

        public LeadDiscoveryAgentResult ToResult(IReadOnlyList<DiscoveredBusinessCandidate> candidates, string model, string? note = null) =>
            new(true, null, candidates, new LeadDiscoveryEvidence(_seenUrls.ToList(), _pages.ToList()), _usage with { WebFetches = _fetches }, model, note);

        private void AddSeen(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url))
                _seenUrls.Add(url);
        }
    }
}
