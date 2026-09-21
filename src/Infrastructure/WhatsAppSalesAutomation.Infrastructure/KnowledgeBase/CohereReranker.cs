using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// Cohere Rerank as the cross-encoder (§L.2).
///
/// Every failure path returns null rather than throwing or inventing scores: not configured, timeout,
/// a 4xx/5xx, an unparseable body, an open circuit. Null is the one signal the caller understands as
/// "switch to the stricter fusion-only gate". Returning zeros instead would look like a reranker that
/// judged every passage irrelevant, and the gate would then refuse to answer for the wrong reason.
///
/// One retry after 300 ms, then give up; a circuit breaker opens after repeated failures so a dead
/// endpoint costs one fast check rather than three timeouts per ticket.
/// </summary>
public sealed class CohereReranker : IReranker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http;
    private readonly RerankerOptions _options;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<CohereReranker> _logger;

    // Shared across instances on purpose: the breaker guards the ENDPOINT, and this type is
    // registered per scope. A per-instance counter would reset on every request and never open.
    private static int _consecutiveFailures;
    private static DateTime _openUntilUtc = DateTime.MinValue;

    public CohereReranker(HttpClient http, IOptions<RerankerOptions> options, IDateTimeProvider clock, ILogger<CohereReranker> logger)
    {
        _http = http;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public string ProviderName => _options.IsConfigured ? "Cohere:" + _options.Model : "None";

    public async Task<IReadOnlyList<double>?> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured || documents.Count == 0 || string.IsNullOrWhiteSpace(query))
            return null;

        if (_clock.UtcNow < _openUntilUtc)
            return null;   // circuit open: do not even try

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var scores = await TryOnceAsync(query, documents, cancellationToken);
            if (scores is not null)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return scores;
            }

            if (attempt == 0)
                await Task.Delay(RetryDelay, cancellationToken);
        }

        if (Interlocked.Increment(ref _consecutiveFailures) >= _options.CircuitBreakerThreshold)
        {
            _openUntilUtc = _clock.UtcNow.AddSeconds(_options.CircuitOpenSeconds);
            _logger.LogWarning("Reranker circuit opened for {Seconds}s after repeated failures.", _options.CircuitOpenSeconds);
        }

        return null;
    }

    private async Task<IReadOnlyList<double>?> TryOnceAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var uri = new Uri(new Uri(_options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/"), "v2/rerank");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new
                {
                    model = _options.Model,
                    query,
                    documents,
                    top_n = documents.Count
                }, options: Json)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Rerank call failed ({Status}).", (int)response.StatusCode);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<CohereResponse>(Json, timeout.Token);
            if (parsed?.Results is null || parsed.Results.Count != documents.Count)
            {
                // A partial answer would misalign scores with documents, which is worse than none.
                _logger.LogWarning("Rerank response had {Got} results for {Expected} documents.", parsed?.Results?.Count ?? 0, documents.Count);
                return null;
            }

            var scores = new double[documents.Count];
            foreach (var result in parsed.Results)
            {
                if (result.Index < 0 || result.Index >= scores.Length)
                    return null;

                scores[result.Index] = result.RelevanceScore;
            }

            return scores;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;   // the caller gave up; that is not the reranker failing

            _logger.LogWarning(ex, "Rerank call failed.");
            return null;
        }
    }

    private sealed class CohereResponse
    {
        [JsonPropertyName("results")]
        public List<CohereResult>? Results { get; set; }
    }

    private sealed class CohereResult
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("relevance_score")]
        public double RelevanceScore { get; set; }
    }

    /// <summary>Test seam: the breaker state is static (see the field comment), so tests that trip it
    /// must be able to put it back.</summary>
    internal static void ResetCircuitForTests()
    {
        _consecutiveFailures = 0;
        _openUntilUtc = DateTime.MinValue;
    }
}
