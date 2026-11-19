using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// <see cref="ILeadDiscoveryAgent"/> on Google's Gemini API, the counterpart of <see cref="OpenAiLeadDiscoveryAgent"/> and
/// <see cref="AnthropicLeadDiscoveryAgent"/>.
///
/// Gemini cannot be given Google Search grounding and function declarations in one request on every model, so a round is two steps:
/// 1. Research - one request with only the <c>google_search</c> tool. The model searches and writes down the businesses it found and the
///    pages that show their contact details.
/// 2. Verify and submit - a function-calling conversation over those notes with the client-side <c>fetch_page</c> reader (the same
///    <see cref="LeadDiscoveryPageFetcher"/> ChatGPT uses, because LeadQualification checks phone numbers and emails against fetched page
///    text) and the strict <c>submit_leads</c> function. The run ends as soon as that call arrives; a turn that ends without one gets a
///    nudge, and then submission is forced with <c>toolConfig</c>.
///
/// The key, base URL and On/Off switch are the Google ones saved under Configuration > AI Providers.
/// </summary>
public class GeminiLeadDiscoveryAgent : ILeadDiscoveryAgent
{
    private const int MaxErrorBodyLength = 500;

    private readonly HttpClient _httpClient;
    private readonly LeadDiscoveryAgentSettings _settings;
    private readonly ILogger<GeminiLeadDiscoveryAgent> _logger;
    private readonly IPlatformAiConfigProvider _aiConfig;
    private readonly LeadDiscoveryPageFetcher _fetcher;

    public GeminiLeadDiscoveryAgent(
        HttpClient httpClient,
        IOptionsSnapshot<LeadDiscoveryAgentSettings> settings,
        ILogger<GeminiLeadDiscoveryAgent> logger,
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

        if (!config.GoogleEnabled)
            return LeadDiscoveryAgentResult.NotConfigured("Google (Gemini) is turned off under Configuration > AI Providers, so lead discovery cannot run.");

        var apiKey = Blank(_settings.GeminiApiKey) ?? config.GoogleApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return LeadDiscoveryAgentResult.NotConfigured(
                "lead discovery has no Gemini API key - save one under Configuration > AI Providers (Google), or set LeadDiscovery:Agent:GeminiApiKey (environment variable LeadDiscovery__Agent__GeminiApiKey).");
        }

        var model = Blank(_settings.GeminiModel) ?? "gemini-2.5-flash";
        var baseUrl = Blank(config.GoogleBaseUrl) ?? "https://generativelanguage.googleapis.com/v1beta/";
        var evidence = new Evidence();

        // ---- Step 1: research with Google Search.
        var notes = await ResearchAsync(apiKey, baseUrl, model, request, evidence, cancellationToken);

        // ---- Step 2: verify the pages and submit.
        var contents = new JsonArray
        {
            UserContent(LeadDiscoveryPrompts.BuildUserMessage(request) + LeadDiscoveryPrompts.GeminiResearchNotes(notes.Text, notes.Sources))
        };
        var nudged = false;

        for (var turn = 1; turn <= _settings.MaxTurns; turn++)
        {
            var response = await SendAsync(apiKey, baseUrl, model, LeadDiscoveryPrompts.GeminiSystemPrompt, contents, FunctionTools(), forceSubmit: nudged, cancellationToken);
            evidence.AddUsage(response["usageMetadata"], searches: 0);

            var content = FirstContent(response);
            var parts = (content?["parts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var calls = parts.Select(part => part["functionCall"] as JsonObject).Where(call => call is not null).Select(call => call!).ToList();

            var submission = calls.FirstOrDefault(call => Str(call["name"]) == LeadDiscoveryPrompts.SubmitToolName);
            if (submission is not null)
            {
                var submitted = submission["args"];
                var candidates = LeadSubmission.Parse(submitted);
                LeadSubmission.LogOutcome(_logger, "Gemini", candidates.Count, request.MaxCandidates, submitted);
                return evidence.ToResult(candidates, model, LeadSubmission.NoteOf(submitted));
            }

            if (calls.Count > 0)
            {
                // The model's own turn goes back unchanged (Gemini 3 needs its thought signatures echoed), then the answers to its fetches.
                contents.Add(content!.DeepClone());
                var answers = await Task.WhenAll(calls.Select(call => AnswerAsync(call, evidence, cancellationToken)));
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(answers.Select(answer => (JsonNode)answer).ToArray()) });
                continue;
            }

            _logger.LogWarning(
                "Gemini lead discovery turn {Turn} ended with no tool call: finish reason {FinishReason}, block reason {BlockReason}, parts [{Parts}]",
                turn,
                Str(response["candidates"]?[0]?["finishReason"]),
                Str(response["promptFeedback"]?["blockReason"]),
                string.Join(", ", parts.Select(part => part.First().Key)));

            if (nudged)
                break;

            nudged = true;
            if (content is not null)
                contents.Add(content.DeepClone());
            contents.Add(UserContent(LeadDiscoveryPrompts.SubmitNudge));
        }

        _logger.LogWarning("Gemini lead discovery round ended without a {ToolName} call after {MaxTurns} turns", LeadDiscoveryPrompts.SubmitToolName, _settings.MaxTurns);
        return evidence.ToResult(Array.Empty<DiscoveredBusinessCandidate>(), model, LeadSubmission.NoSubmissionNote);
    }

    private async Task<(string Text, IReadOnlyList<string> Sources)> ResearchAsync(
        string apiKey, string baseUrl, string model, LeadDiscoveryAgentRequest request, Evidence evidence, CancellationToken cancellationToken)
    {
        var contents = new JsonArray { UserContent(LeadDiscoveryPrompts.BuildUserMessage(request)) };
        var tools = new JsonArray { new JsonObject { ["google_search"] = new JsonObject() } };

        var response = await SendAsync(apiKey, baseUrl, model, LeadDiscoveryPrompts.GeminiResearchPrompt, contents, tools, forceSubmit: false, cancellationToken);

        // Google bills grounded prompts, not individual queries, so one grounded request is counted as one search.
        evidence.AddUsage(response["usageMetadata"], searches: 1);

        var text = new StringBuilder();
        foreach (var part in (FirstContent(response)?["parts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (Str(part["text"]) is { } piece && part["thought"] is not JsonValue)
                text.Append(piece);
        }

        var sources = new List<string>();
        var grounding = response["candidates"]?[0]?["groundingMetadata"] as JsonObject;
        foreach (var chunk in (grounding?["groundingChunks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (Str(chunk["web"]?["uri"]) is { } uri)
            {
                sources.Add(uri);
                evidence.AddSeen(uri);
            }
        }

        return (text.ToString().Trim(), sources);
    }

    /// <summary>The function_response part for one call (an error string when the call cannot be served, so the model carries on).</summary>
    private async Task<JsonObject> AnswerAsync(JsonObject call, Evidence evidence, CancellationToken cancellationToken)
    {
        string output;

        if (Str(call["name"]) != LeadDiscoveryPrompts.FetchToolName)
        {
            output = $"error: unknown function {Str(call["name"])}";
        }
        else if (Str(call["args"]?["url"]) is not { } url)
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

        return new JsonObject
        {
            ["functionResponse"] = new JsonObject
            {
                ["name"] = Str(call["name"]) ?? LeadDiscoveryPrompts.FetchToolName,
                ["response"] = new JsonObject { ["result"] = output }
            }
        };
    }

    private async Task<JsonObject> SendAsync(
        string apiKey, string baseUrl, string model, string systemPrompt, JsonArray contents, JsonArray tools, bool forceSubmit, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = contents.DeepClone(),
            ["tools"] = tools.DeepClone(),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = _settings.MaxTokens }
        };

        // After the nudge the model has to submit: only submit_leads may be called.
        if (forceSubmit)
        {
            payload["toolConfig"] = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject
                {
                    ["mode"] = "ANY",
                    ["allowedFunctionNames"] = new JsonArray(LeadDiscoveryPrompts.SubmitToolName)
                }
            };
        }

        var uri = new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(baseUrl)), $"models/{Uri.EscapeDataString(model)}:generateContent");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("x-goog-api-key", apiKey);

        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            // Thrown rather than swallowed so the run is recorded as Failed with Google's own reason (an invalid key, no quota, a model without search).
            var detail = body.Length > MaxErrorBodyLength ? body[..MaxErrorBodyLength] : body;
            throw new HttpRequestException($"Gemini lead discovery request failed ({(int)httpResponse.StatusCode}): {detail}", null, httpResponse.StatusCode);
        }

        var response = JsonNode.Parse(body) as JsonObject
                       ?? throw new System.Text.Json.JsonException("Gemini returned a response that is not a JSON object.");

        if (Str(response["promptFeedback"]?["blockReason"]) is { } blocked)
            throw new InvalidOperationException($"Gemini declined the lead discovery request: {blocked}");

        return response;
    }

    private JsonArray FunctionTools() => new()
    {
        new JsonObject
        {
            ["functionDeclarations"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = LeadDiscoveryPrompts.FetchToolName,
                    ["description"] = LeadDiscoveryPrompts.FetchToolDescription,
                    ["parameters"] = GeminiSchema.From(LeadDiscoveryPrompts.FetchToolInputSchema)
                },
                new JsonObject
                {
                    ["name"] = LeadDiscoveryPrompts.SubmitToolName,
                    ["description"] = LeadDiscoveryPrompts.SubmitToolDescription,
                    ["parameters"] = GeminiSchema.From(LeadDiscoveryPrompts.SubmitToolInputSchema)
                }
            }
        }
    };

    private static JsonObject? FirstContent(JsonObject response) => response["candidates"]?[0]?["content"] as JsonObject;

    private static JsonObject UserContent(string text) =>
        new() { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    /// <summary>Accumulates what a round actually looked at and used, across both steps.</summary>
    private sealed class Evidence
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _seenUrls = new(StringComparer.Ordinal);
        private readonly List<FetchedPage> _pages = new();
        private LeadDiscoveryUsage _usage = LeadDiscoveryUsage.None;
        private int _fetches;

        public void AddUsage(JsonNode? usage, int searches)
        {
            var prompt = 0;
            var cached = 0;
            var output = 0;
            if (usage is JsonObject metadata)
            {
                prompt = Int(metadata["promptTokenCount"]);
                cached = Int(metadata["cachedContentTokenCount"]);
                // Thinking tokens are billed as output.
                output = Int(metadata["candidatesTokenCount"]) + Int(metadata["thoughtsTokenCount"]);
            }

            // Gemini counts cached tokens inside promptTokenCount; the usage record keeps them apart.
            lock (_gate)
                _usage += new LeadDiscoveryUsage(Math.Max(0, prompt - cached), output, cached, 0, searches, 0);
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

        public void AddSeen(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            lock (_gate)
                _seenUrls.Add(url);
        }

        public LeadDiscoveryAgentResult ToResult(IReadOnlyList<DiscoveredBusinessCandidate> candidates, string model, string? note = null)
        {
            lock (_gate)
                return new(true, null, candidates, new LeadDiscoveryEvidence(_seenUrls.ToList(), _pages.ToList()), _usage with { WebFetches = _fetches }, model, note);
        }
    }
}

/// <summary>Turns the JSON Schema the other agents use into the OpenAPI subset Gemini accepts: no <c>additionalProperties</c>, and a
/// nullable type written <c>"nullable": true</c> instead of a <c>["string", "null"]</c> union.</summary>
internal static class GeminiSchema
{
    public static JsonNode From(string jsonSchema) => Convert(JsonNode.Parse(jsonSchema)!)!;

    private static JsonNode? Convert(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject source:
            {
                var result = new JsonObject();
                foreach (var (key, value) in source)
                {
                    if (key == "additionalProperties")
                        continue;

                    if (key == "type" && value is JsonArray types)
                    {
                        var names = types.Select(type => type?.GetValue<string>()).Where(name => name is not null).ToList();
                        result["type"] = names.FirstOrDefault(name => name != "null") ?? "string";
                        if (names.Contains("null"))
                            result["nullable"] = true;
                        continue;
                    }

                    result[key] = Convert(value);
                }
                return result;
            }
            case JsonArray array:
                return new JsonArray(array.Select(Convert).ToArray());
            default:
                return node?.DeepClone();
        }
    }
}
