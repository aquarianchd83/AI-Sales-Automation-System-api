using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Lists the models each provider offers by asking the provider (Claude: <c>GET /models</c>, ChatGPT: <c>GET /models</c>, Gemini: <c>GET /models</c>) with
/// the key saved under Configuration &gt; AI Providers. Only text-generation models are kept: ChatGPT's list also holds embeddings, speech, images and
/// moderation, and Gemini's holds embedding and image models. A list is kept for an hour per provider and key, so the AI Providers page does not call the
/// provider on every visit; a provider that cannot be reached gives an empty list, never an error.
/// </summary>
public sealed class ProviderModelCatalog : IProviderModelCatalog
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (DateTime FetchedAtUtc, IReadOnlyList<string> Models)> Cache = new();

    private static readonly Regex OpenAiChatModel = new(@"^(gpt-|chatgpt-|o\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] NotForText =
    {
        "embedding", "audio", "realtime", "transcribe", "tts", "whisper", "image", "dall-e", "moderation", "search-preview", "instruct", "aqa", "imagen", "veo", "live", "robotics", "computer-use",
    };

    private readonly HttpClient _http;
    private readonly IPlatformAiConfigProvider _config;
    private readonly ILogger<ProviderModelCatalog> _logger;

    public ProviderModelCatalog(HttpClient http, IPlatformAiConfigProvider config, ILogger<ProviderModelCatalog> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string provider, CancellationToken cancellationToken = default)
    {
        var credentials = _config.Get();
        var key = provider switch
        {
            "Anthropic" => credentials.AnthropicApiKey,
            "OpenAI" => credentials.OpenAiApiKey,
            "Google" => credentials.GoogleApiKey,
            _ => null,
        };

        // An Off provider has no key in the credentials, so it is never called.
        if (string.IsNullOrWhiteSpace(key))
            return Array.Empty<string>();

        // The cache is keyed by a short fingerprint of the key, so a changed key is a new list and the key itself is never held as a key.
        var cacheKey = $"{provider}:{Math.Abs(key.GetHashCode())}";
        if (Cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.FetchedAtUtc < Lifetime)
            return cached.Models;

        try
        {
            using var request = provider switch
            {
                "Anthropic" => Anthropic(credentials, key),
                "OpenAI" => OpenAi(credentials, key),
                _ => Google(credentials, key),
            };

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Provider} model list answered HTTP {Status}; offering the priced models only", provider, (int)response.StatusCode);
                return Array.Empty<string>();
            }

            var models = Parse(provider, JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)));
            Cache[cacheKey] = (DateTime.UtcNow, models);
            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "{Provider} model list could not be read; offering the priced models only", provider);
            return Array.Empty<string>();
        }
    }

    /// <summary>The model ids in a provider's list response that are for generating text. Public so the filtering can be tested without a network.</summary>
    public static IReadOnlyList<string> Parse(string provider, JsonNode? body)
    {
        var ids = new List<string>();

        if (provider == "Google")
        {
            foreach (var model in (body?["models"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var methods = (model["supportedGenerationMethods"] as JsonArray ?? new JsonArray()).Select(m => (string?)m).ToList();
                var name = (string?)model["name"];
                if (name is not null && methods.Contains("generateContent"))
                    ids.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name);
            }
        }
        else
        {
            foreach (var model in (body?["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if ((string?)model["id"] is { } id)
                    ids.Add(id);
            }

            if (provider == "OpenAI")
                ids = ids.Where(id => OpenAiChatModel.IsMatch(id)).ToList();
        }

        return ids
            .Where(id => !NotForText.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HttpRequestMessage Anthropic(AiCredentials c, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.AnthropicBaseUrl)), "models?limit=1000"));
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", string.IsNullOrWhiteSpace(c.AnthropicApiVersion) ? "2023-06-01" : c.AnthropicApiVersion);
        return request;
    }

    private static HttpRequestMessage OpenAi(AiCredentials c, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.OpenAiBaseUrl)), "models"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private static HttpRequestMessage Google(AiCredentials c, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.GoogleBaseUrl)), "models?pageSize=1000"));
        request.Headers.Add("x-goog-api-key", key);
        return request;
    }
}
