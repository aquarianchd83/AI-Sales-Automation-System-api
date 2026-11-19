using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Lead discovery on Gemini: a Google Search step, then a function-calling step that reads pages and submits. The Gemini API is scripted.</summary>
public sealed class GeminiLeadDiscoveryAgentTests
{
    private static readonly LeadDiscoveryAgentRequest Request = new(
        "dental clinic", new[] { "dentist" }, new[] { "Pune" }, 3,
        Array.Empty<string>(), false, false, false, 0, Array.Empty<string>(), Array.Empty<string>());

    private sealed class Keys : IPlatformAiConfigProvider
    {
        private readonly string? _googleKey;
        private readonly bool _enabled;

        public Keys(string? googleKey, bool enabled = true)
        {
            _googleKey = googleKey;
            _enabled = enabled;
        }

        public AiCredentials Get() => new("Simulated", "Simulated", null, "m", "v", "https://api.anthropic.com/v1/",
            null, "m", "e", "https://api.openai.com/v1/", _googleKey, "m", "e", "https://generativelanguage.googleapis.com/v1beta/", GoogleEnabled: _enabled);
    }

    private sealed class FixedSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public FixedSnapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }

    /// <summary>Answers each request with the next scripted body and keeps what was sent.</summary>
    private sealed class ScriptedGemini : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<JsonObject> Sent { get; } = new();
        public List<Uri> Uris { get; } = new();
        public List<string?> Keys { get; } = new();

        public ScriptedGemini(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            Uris.Add(request.RequestUri!);
            Keys.Add(request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.First() : null);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }

    private static GeminiLeadDiscoveryAgent Agent(ScriptedGemini handler, string? key = "AIza-test", bool enabled = true, LeadDiscoveryAgentSettings? settings = null) =>
        new(new HttpClient(handler), new FixedSnapshot<LeadDiscoveryAgentSettings>(settings ?? new LeadDiscoveryAgentSettings()),
            NullLogger<GeminiLeadDiscoveryAgent>.Instance, new Keys(key, enabled),
            new LeadDiscoveryPageFetcher(new HttpClient(LeadDiscoveryPageFetcher.CreateHandler())));

    private static string Research(string text = "1. Smile Dental, Pune - https://smile.example/contact", int prompt = 1000, int output = 200) =>
        new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["groundingMetadata"] = new JsonObject
                {
                    ["groundingChunks"] = new JsonArray(new JsonObject { ["web"] = new JsonObject { ["uri"] = "https://smile.example/contact", ["title"] = "smile.example" } })
                }
            }),
            ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = prompt, ["candidatesTokenCount"] = output, ["thoughtsTokenCount"] = 50 }
        }.ToJsonString();

    private static string Call(string name, JsonObject args, int prompt = 500, int output = 100) =>
        new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["role"] = "model",
                    ["parts"] = new JsonArray(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = name, ["args"] = args } })
                }
            }),
            ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = prompt, ["candidatesTokenCount"] = output }
        }.ToJsonString();

    private static string Text(string text) =>
        new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["finishReason"] = "STOP"
            }),
            ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = 300, ["candidatesTokenCount"] = 20 }
        }.ToJsonString();

    private static JsonObject Submission(params string[] names) => new()
    {
        ["note"] = "Searched dentists in Pune.",
        ["candidates"] = new JsonArray(names.Select(name => (JsonNode)new JsonObject
        {
            ["businessName"] = name,
            ["businessType"] = "Dental clinic",
            ["contactPerson"] = null,
            ["address"] = "MG Road",
            ["city"] = "Pune",
            ["state"] = "Maharashtra",
            ["phone"] = null,
            ["phoneSourceUrl"] = null,
            ["email"] = null,
            ["website"] = "https://smile.example",
            ["sourceUrl"] = "https://smile.example/contact",
            ["isIndependentBusiness"] = true,
            ["isPermanentlyClosed"] = false,
            ["leadScore"] = 80,
            ["scoreRationale"] = "Matches the profile."
        }).ToArray())
    };

    [Fact]
    public async Task Researches_with_Google_Search_then_submits_what_it_found()
    {
        var handler = new ScriptedGemini(Research(), Call("submit_leads", Submission("Smile Dental")));

        var result = await Agent(handler).DiscoverAsync(Request);

        Assert.True(result.IsConfigured);
        Assert.Equal("gemini-2.5-flash", result.Model);
        Assert.Equal("Smile Dental", Assert.Single(result.Candidates).BusinessName);
        Assert.Equal("Searched dentists in Pune.", result.Note);
        Assert.Contains("https://smile.example/contact", result.Evidence.SeenUrls);
    }

    [Fact]
    public async Task The_research_request_has_only_Google_Search_and_the_second_has_only_the_functions()
    {
        var handler = new ScriptedGemini(Research(), Call("submit_leads", Submission()));

        await Agent(handler).DiscoverAsync(Request);

        var research = (JsonArray)handler.Sent[0]["tools"]!;
        Assert.NotNull(research[0]!["google_search"]);
        Assert.Null(research[0]!["functionDeclarations"]);

        var verify = (JsonArray)handler.Sent[1]["tools"]!;
        Assert.Null(verify[0]!["google_search"]);
        var names = ((JsonArray)verify[0]!["functionDeclarations"]!).Select(f => (string?)f!["name"]).ToArray();
        Assert.Equal(new[] { "fetch_page", "submit_leads" }, names);
    }

    [Fact]
    public async Task The_research_notes_and_source_links_are_handed_to_the_second_step()
    {
        var handler = new ScriptedGemini(Research("1. Smile Dental, Pune"), Call("submit_leads", Submission()));

        await Agent(handler).DiscoverAsync(Request);

        var message = (string)handler.Sent[1]["contents"]![0]!["parts"]![0]!["text"]!;
        Assert.Contains("1. Smile Dental, Pune", message);
        Assert.Contains("- https://smile.example/contact", message);
    }

    [Fact]
    public async Task Calls_Gemini_with_the_saved_key_in_a_header_and_never_in_the_url()
    {
        var handler = new ScriptedGemini(Research(), Call("submit_leads", Submission()));

        await Agent(handler).DiscoverAsync(Request);

        Assert.All(handler.Keys, key => Assert.Equal("AIza-test", key));
        Assert.All(handler.Uris, uri =>
        {
            Assert.EndsWith("models/gemini-2.5-flash:generateContent", uri.AbsolutePath);
            Assert.DoesNotContain("AIza-test", uri.ToString());
        });
    }

    [Fact]
    public async Task Counts_every_token_of_both_steps_and_one_grounded_search()
    {
        var handler = new ScriptedGemini(Research(prompt: 1000, output: 200), Call("submit_leads", Submission(), prompt: 500, output: 100));

        var result = await Agent(handler).DiscoverAsync(Request);

        Assert.Equal(1500, result.Usage.InputTokens);
        Assert.Equal(200 + 50 + 100, result.Usage.OutputTokens); // thinking tokens are billed as output
        Assert.Equal(1, result.Usage.WebSearches);
        Assert.Equal(0, result.Usage.WebFetches);
    }

    [Fact]
    public async Task A_fetch_is_answered_with_a_function_response_and_the_model_turn_is_sent_back_unchanged()
    {
        var fetch = Call("fetch_page", new JsonObject { ["url"] = "http://127.0.0.1/contact" });
        var handler = new ScriptedGemini(Research(), fetch, Call("submit_leads", Submission()));

        var result = await Agent(handler).DiscoverAsync(Request);

        var third = (JsonArray)handler.Sent[2]["contents"]!;
        Assert.Equal(3, third.Count); // the request, the model's call, our answer
        Assert.Equal("model", (string?)third[1]!["role"]);
        Assert.Equal("fetch_page", (string?)third[1]!["parts"]![0]!["functionCall"]!["name"]);
        Assert.Equal("fetch_page", (string?)third[2]!["parts"]![0]!["functionResponse"]!["name"]);
        Assert.StartsWith("error: could not read", (string?)third[2]!["parts"]![0]!["functionResponse"]!["response"]!["result"]);
        Assert.Equal(1, result.Usage.WebFetches);
    }

    [Fact]
    public async Task Stops_fetching_at_the_round_limit_and_tells_the_model_to_submit()
    {
        var fetch = Call("fetch_page", new JsonObject { ["url"] = "http://127.0.0.1/a" });
        var handler = new ScriptedGemini(Research(), fetch, Call("submit_leads", Submission()));

        await Agent(handler, settings: new LeadDiscoveryAgentSettings { MaxFetchesPerRound = 0 }).DiscoverAsync(Request);

        var answer = (string?)handler.Sent[2]["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!["result"];
        Assert.Contains("fetch limit", answer);
        Assert.Contains("submit_leads", answer);
    }

    [Fact]
    public async Task A_turn_with_no_tool_call_is_nudged_and_then_forced_to_submit()
    {
        var handler = new ScriptedGemini(Research(), Text("I looked into it."), Call("submit_leads", Submission("Smile Dental")));

        var result = await Agent(handler).DiscoverAsync(Request);

        Assert.Null(handler.Sent[1]["toolConfig"]);
        var forced = handler.Sent[2]["toolConfig"]!["functionCallingConfig"]!;
        Assert.Equal("ANY", (string?)forced["mode"]);
        Assert.Equal("submit_leads", (string?)forced["allowedFunctionNames"]![0]);
        Assert.Contains("Stop researching", (string?)handler.Sent[2]["contents"]!.AsArray().Last()!["parts"]![0]!["text"]);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task Gives_up_with_an_explanation_when_it_never_submits()
    {
        var handler = new ScriptedGemini(Research(), Text("one"), Text("two"));

        var result = await Agent(handler).DiscoverAsync(Request);

        Assert.True(result.IsConfigured);
        Assert.Empty(result.Candidates);
        Assert.Equal(LeadSubmission.NoSubmissionNote, result.Note);
    }

    [Fact]
    public async Task Is_not_configured_without_a_key_or_when_Google_is_switched_off()
    {
        var noKey = await Agent(new ScriptedGemini(), key: null).DiscoverAsync(Request);
        var off = await Agent(new ScriptedGemini(), enabled: false).DiscoverAsync(Request);

        Assert.False(noKey.IsConfigured);
        Assert.Contains("no Gemini API key", noKey.NotConfiguredReason);
        Assert.False(off.IsConfigured);
        Assert.Contains("turned off", off.NotConfiguredReason);
    }

    [Fact]
    public async Task A_rejected_request_fails_the_run_with_Googles_own_reason()
    {
        var handler = new ThrowingHandler();
        var agent = new GeminiLeadDiscoveryAgent(new HttpClient(handler), new FixedSnapshot<LeadDiscoveryAgentSettings>(new LeadDiscoveryAgentSettings()),
            NullLogger<GeminiLeadDiscoveryAgent>.Instance, new Keys("AIza-test"),
            new LeadDiscoveryPageFetcher(new HttpClient(LeadDiscoveryPageFetcher.CreateHandler())));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => agent.DiscoverAsync(Request));

        Assert.Contains("403", error.Message);
        Assert.Contains("API key not valid", error.Message);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"message\":\"API key not valid\"}}") });
    }

    [Fact]
    public void The_function_schemas_are_translated_to_what_Gemini_accepts()
    {
        var schema = (JsonObject)GeminiSchema.From(LeadDiscoveryPrompts.SubmitToolInputSchema);
        var json = schema.ToJsonString();

        Assert.DoesNotContain("additionalProperties", json);
        Assert.DoesNotContain("\"null\"", json);

        var phone = schema["properties"]!["candidates"]!["items"]!["properties"]!["phone"]!;
        Assert.Equal("string", (string?)phone["type"]);
        Assert.True((bool?)phone["nullable"]);

        var name = schema["properties"]!["candidates"]!["items"]!["properties"]!["businessName"]!;
        Assert.Null(name["nullable"]);

        var flag = schema["properties"]!["candidates"]!["items"]!["properties"]!["isPermanentlyClosed"]!;
        Assert.Equal("boolean", (string?)flag["type"]);
        Assert.True((bool?)flag["nullable"]);
    }

    [Fact]
    public void Gemini_search_is_priced_per_grounded_prompt_at_Googles_rate_not_the_general_one()
    {
        var pricing = new LeadDiscoveryPricingOptions();
        var usage = new LeadDiscoveryUsage(0, 0, 0, 0, WebSearches: 2, WebFetches: 0);

        Assert.Equal(0.07m, LeadDiscoveryCost.SearchCost(usage, pricing, "gemini-2.5-flash"));
        Assert.Equal(0.02m, LeadDiscoveryCost.SearchCost(usage, pricing, "gpt-5-mini")); // a model with no override keeps the general rate
        Assert.Equal(0.02m, LeadDiscoveryCost.SearchCost(usage, pricing));
    }

    [Fact]
    public void Gemini_models_have_their_own_token_rates()
    {
        var pricing = new LeadDiscoveryPricingOptions();
        var usage = new LeadDiscoveryUsage(1_000_000, 1_000_000, 0, 0, 0, 0);

        Assert.Equal(2.8m, LeadDiscoveryCost.Estimate(usage, "gemini-2.5-flash", pricing));
        Assert.Equal(11.25m, LeadDiscoveryCost.Estimate(usage, "gemini-2.5-pro", pricing));
    }
}
