using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real Google embeddings client, selected via <c>AiProviders:EmbeddingProvider = "Google"</c> -
/// independent of <c>AiProviders:Provider</c>, same reasoning as OpenAiEmbeddingClient. Never
/// exercised against a live API key in this codebase - same caveat as the chat clients.
/// </summary>
public class GoogleEmbeddingClient : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly GoogleAiSettings _settings;
    private readonly ILogger<GoogleEmbeddingClient> _logger;

    public GoogleEmbeddingClient(HttpClient httpClient, IOptions<AiProviderSettings> settings, ILogger<GoogleEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value.Google;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var payload = new { content = new { parts = new[] { new { text } } } };

        try
        {
            var requestUri = $"models/{_settings.EmbeddingModel}:embedContent?key={Uri.EscapeDataString(_settings.ApiKey)}";
            using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google embeddings call failed ({Status}): {Body}", response.StatusCode, body);
                return Array.Empty<float>();
            }

            var parsed = JsonSerializer.Deserialize<GoogleEmbedContentResponse>(body, JsonOptions);
            return parsed?.Embedding?.Values ?? Array.Empty<float>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // See OpenAiEmbeddingClient's doc comment on this catch - empty vector, not a throw.
            _logger.LogWarning(ex, "Google embeddings call threw");
            return Array.Empty<float>();
        }
    }

    private class GoogleEmbedContentResponse
    {
        [JsonPropertyName("embedding")]
        public GoogleEmbeddingValues? Embedding { get; set; }
    }

    private class GoogleEmbeddingValues
    {
        [JsonPropertyName("values")]
        public float[]? Values { get; set; }
    }
}
