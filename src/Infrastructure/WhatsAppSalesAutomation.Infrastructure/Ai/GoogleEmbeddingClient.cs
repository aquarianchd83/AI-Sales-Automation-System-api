using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real Google embeddings client. No longer implements <see cref="IEmbeddingService"/> directly - see
/// <see cref="OpenAiEmbeddingClient"/>'s own doc comment for why. Never exercised against a live API
/// key in this codebase - same caveat as the chat clients.
/// </summary>
public class GoogleEmbeddingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleEmbeddingClient> _logger;

    public GoogleEmbeddingClient(HttpClient httpClient, ILogger<GoogleEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(TenantAiCredentials credentials, string text, CancellationToken cancellationToken)
    {
        var payload = new { content = new { parts = new[] { new { text } } } };

        try
        {
            var baseUri = new Uri(AiPromptSupport.EnsureTrailingSlash(credentials.GoogleBaseUrl));
            var uri = new Uri(baseUri, $"models/{credentials.GoogleEmbeddingModel}:embedContent?key={Uri.EscapeDataString(credentials.GoogleApiKey ?? string.Empty)}");
            using var response = await _httpClient.PostAsJsonAsync(uri, payload, JsonOptions, cancellationToken);
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
