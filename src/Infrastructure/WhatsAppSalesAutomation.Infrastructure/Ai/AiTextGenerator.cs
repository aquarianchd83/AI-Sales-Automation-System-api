using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// One-shot text with the calling tenant's own AI provider (the same credentials its customer conversations use).
/// Returns null - never throws - when the provider is Simulated, has no key, or the call fails, so a helper feature
/// can fall back instead of breaking the screen.
///
/// Keys travel in headers, never the URL (Google's key is usually a query parameter, which would be written to the
/// log by the HTTP client's default logging), and this client's loggers are removed regardless.
/// </summary>
public class AiTextGenerator : IAiTextGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ITenantAiConfigProvider _configProvider;
    private readonly ILogger<AiTextGenerator> _logger;

    public AiTextGenerator(HttpClient httpClient, ITenantAiConfigProvider configProvider, ILogger<AiTextGenerator> logger)
    {
        _httpClient = httpClient;
        _configProvider = configProvider;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<string?> GenerateAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        if (credentials is null)
            return null;

        try
        {
            return credentials.Provider.ToLowerInvariant() switch
            {
                "anthropic" when !string.IsNullOrWhiteSpace(credentials.AnthropicApiKey)
                    => await AnthropicAsync(credentials, systemPrompt, userPrompt, maxTokens, cancellationToken),
                "openai" when !string.IsNullOrWhiteSpace(credentials.OpenAiApiKey)
                    => await OpenAiAsync(credentials, systemPrompt, userPrompt, maxTokens, cancellationToken),
                "google" when !string.IsNullOrWhiteSpace(credentials.GoogleApiKey)
                    => await GoogleAsync(credentials, systemPrompt, userPrompt, maxTokens, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Text generation with the {Provider} provider failed", credentials.Provider);
            return null;
        }
    }

    private async Task<string?> AnthropicAsync(TenantAiCredentials c, string system, string user, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            model = c.AnthropicModel,
            max_tokens = maxTokens,
            system,
            messages = new[] { new { role = "user", content = user } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.AnthropicBaseUrl)), "messages"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("x-api-key", c.AnthropicApiKey);
        request.Headers.Add("anthropic-version", c.AnthropicApiVersion);

        var body = await SendAsync(request, "Anthropic", ct);
        return body is null
            ? null
            : JsonSerializer.Deserialize<AnthropicReply>(body, JsonOptions)?.Content?.FirstOrDefault(b => b.Type == "text")?.Text;
    }

    private async Task<string?> OpenAiAsync(TenantAiCredentials c, string system, string user, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            // No token cap, like the conversation client: the prompt itself asks for a short list, and not every
            // OpenAI-compatible endpoint accepts the same cap parameter.
            model = c.OpenAiChatModel,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.OpenAiBaseUrl)), "chat/completions"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.OpenAiApiKey);

        var body = await SendAsync(request, "OpenAI", ct);
        return body is null
            ? null
            : JsonSerializer.Deserialize<OpenAiReply>(body, JsonOptions)?.Choices?.FirstOrDefault()?.Message?.Content;
    }

    private async Task<string?> GoogleAsync(TenantAiCredentials c, string system, string user, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { maxOutputTokens = maxTokens }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(c.GoogleBaseUrl)), $"models/{c.GoogleChatModel}:generateContent"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("x-goog-api-key", c.GoogleApiKey);

        var body = await SendAsync(request, "Google", ct);
        return body is null
            ? null
            : JsonSerializer.Deserialize<GoogleReply>(body, JsonOptions)?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
    }

    private async Task<string?> SendAsync(HttpRequestMessage request, string provider, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return body;

        _logger.LogWarning("{Provider} text generation failed ({Status})", provider, (int)response.StatusCode);
        return null;
    }

    private sealed class AnthropicReply
    {
        [JsonPropertyName("content")]
        public List<AnthropicBlock>? Content { get; set; }
    }

    private sealed class AnthropicBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class OpenAiReply
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class GoogleReply
    {
        [JsonPropertyName("candidates")]
        public List<GoogleCandidate>? Candidates { get; set; }
    }

    private sealed class GoogleCandidate
    {
        [JsonPropertyName("content")]
        public GoogleContent? Content { get; set; }
    }

    private sealed class GoogleContent
    {
        [JsonPropertyName("parts")]
        public List<GooglePart>? Parts { get; set; }
    }

    private sealed class GooglePart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
