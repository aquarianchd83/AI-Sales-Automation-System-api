using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Ai;

/// <summary>
/// Real OpenAI embeddings client, selected via <c>AiProviders:EmbeddingProvider = "OpenAI"</c> -
/// independent of <c>AiProviders:Provider</c>, since Anthropic has no embeddings endpoint (see
/// IEmbeddingService's own doc comment). Never exercised against a live API key in this codebase -
/// same caveat as the chat clients.
/// </summary>
public class OpenAiEmbeddingClient : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly ILogger<OpenAiEmbeddingClient> _logger;

    public OpenAiEmbeddingClient(HttpClient httpClient, IOptions<AiProviderSettings> settings, ILogger<OpenAiEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value.OpenAI;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.openai.com/v1/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var payload = new { model = _settings.EmbeddingModel, input = text };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("embeddings", payload, JsonOptions, cancellationToken);
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
