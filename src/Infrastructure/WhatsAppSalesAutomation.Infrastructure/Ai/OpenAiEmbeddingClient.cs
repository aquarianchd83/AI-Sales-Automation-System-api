using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real OpenAI embeddings client. No longer implements <see cref="IEmbeddingService"/> directly -
/// <see cref="TenantEmbeddingService"/>/<see cref="TenantEmbeddingProviderCatalog"/> are the
/// DI-registered IEmbeddingService/IEmbeddingProviderCatalog and the only callers of this class, each
/// passing the calling tenant's already-resolved <see cref="TenantAiCredentials"/> in. Never exercised
/// against a live API key in this codebase - same caveat as the chat clients.
/// </summary>
public class OpenAiEmbeddingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiEmbeddingClient> _logger;

    public OpenAiEmbeddingClient(HttpClient httpClient, ILogger<OpenAiEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(TenantAiCredentials credentials, string text, CancellationToken cancellationToken)
    {
        var payload = new { model = credentials.OpenAiEmbeddingModel, input = text };

        try
        {
            var uri = new Uri(new Uri(AiPromptSupport.EnsureTrailingSlash(credentials.OpenAiBaseUrl)), "embeddings");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.OpenAiApiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI embeddings call failed ({Status}): {Body}", response.StatusCode, body);
                return Array.Empty<float>();
            }

            var parsed = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(body, JsonOptions);
            return parsed?.Data?.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Empty vector rather than throwing - RetrieveRelevantChunksAsync's cosine similarity
            // naturally scores an all-zero vector at 0 against everything, so a transient embeddings
            // outage just means "no grounding found" for this turn instead of failing the whole
            // inbound-message pipeline the way an uncaught exception would.
            _logger.LogWarning(ex, "OpenAI embeddings call threw");
            return Array.Empty<float>();
        }
    }

    private class OpenAiEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAiEmbeddingData>? Data { get; set; }
    }

    private class OpenAiEmbeddingData
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
