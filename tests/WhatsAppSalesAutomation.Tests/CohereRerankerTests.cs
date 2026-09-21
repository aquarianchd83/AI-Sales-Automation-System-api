using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The reranker's contract is narrow and matters: it returns real scores, or null. Every failure
/// mode - not configured, timeout, HTTP error, malformed or partial body, open circuit - must come
/// back as null, because null is what switches retrieval to its stricter fusion-only gate. Anything
/// else (zeros, a partial list, an exception) either misleads the gate or crashes a ticket reply.
/// </summary>
public class CohereRerankerTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly StubHandler _handler = new();

    public CohereRerankerTests() => CohereReranker.ResetCircuitForTests();

    public void Dispose() => CohereReranker.ResetCircuitForTests();

    private CohereReranker NewReranker(RerankerOptions? options = null) => new(
        new HttpClient(_handler),
        Options.Create(options ?? new RerankerOptions { ApiKey = "key", TimeoutSeconds = 1 }),
        _clock,
        NullLogger<CohereReranker>.Instance);

    private static readonly string[] Docs = { "first", "second", "third" };

    [Fact]
    public async Task Scores_are_returned_aligned_with_the_input_order_not_the_response_order()
    {
        // Cohere returns results sorted by relevance, not by input position.
        _handler.Respond(HttpStatusCode.OK, @"{""results"":[
            {""index"":2,""relevance_score"":0.9},{""index"":0,""relevance_score"":0.5},{""index"":1,""relevance_score"":0.1}]}");

        var scores = await NewReranker().RerankAsync("query", Docs);

        Assert.Equal(new[] { 0.5, 0.1, 0.9 }, scores);
    }

    [Fact]
    public async Task The_request_carries_the_model_query_documents_and_bearer_key()
    {
        _handler.Respond(HttpStatusCode.OK, @"{""results"":[{""index"":0,""relevance_score"":1},{""index"":1,""relevance_score"":1},{""index"":2,""relevance_score"":1}]}");

        await NewReranker().RerankAsync("how do I buy credits", Docs);

        var request = _handler.Requests.Single();
        Assert.EndsWith("/v2/rerank", request.Uri);
        Assert.Equal("Bearer key", request.Authorization);
        Assert.Contains("\"model\":\"rerank-v3.5\"", request.Body);
        Assert.Contains("how do I buy credits", request.Body);
        Assert.Contains("\"top_n\":3", request.Body);
    }

    [Fact]
    public async Task Not_configured_returns_null_without_any_network_call()
    {
        var scores = await NewReranker(new RerankerOptions { ApiKey = "" }).RerankAsync("q", Docs);

        Assert.Null(scores);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task No_documents_returns_null_without_any_network_call()
    {
        Assert.Null(await NewReranker().RerankAsync("q", Array.Empty<string>()));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_server_error_is_retried_once_and_then_reported_as_unavailable()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, "{}");

        Assert.Null(await NewReranker().RerankAsync("q", Docs));
        Assert.Equal(2, _handler.Requests.Count);   // one retry, not a hammering
    }

    [Fact]
    public async Task A_transient_failure_that_recovers_on_the_retry_returns_real_scores()
    {
        _handler.Respond(HttpStatusCode.BadGateway, "{}");
        _handler.Respond(HttpStatusCode.OK, @"{""results"":[{""index"":0,""relevance_score"":0.8},{""index"":1,""relevance_score"":0.2},{""index"":2,""relevance_score"":0.4}]}");

        Assert.Equal(new[] { 0.8, 0.2, 0.4 }, await NewReranker().RerankAsync("q", Docs));
    }

    [Fact]
    public async Task A_partial_response_is_rejected_because_misaligned_scores_are_worse_than_none()
    {
        _handler.Respond(HttpStatusCode.OK, @"{""results"":[{""index"":0,""relevance_score"":0.9}]}");

        Assert.Null(await NewReranker().RerankAsync("q", Docs));
    }

    [Fact]
    public async Task An_out_of_range_index_is_rejected()
    {
        _handler.Respond(HttpStatusCode.OK, @"{""results"":[{""index"":0,""relevance_score"":1},{""index"":1,""relevance_score"":1},{""index"":9,""relevance_score"":1}]}");

        Assert.Null(await NewReranker().RerankAsync("q", Docs));
    }

    [Fact]
    public async Task A_malformed_body_is_reported_as_unavailable_not_thrown()
    {
        _handler.Respond(HttpStatusCode.OK, "this is not json");

        Assert.Null(await NewReranker().RerankAsync("q", Docs));
    }

    [Fact]
    public async Task A_timeout_is_reported_as_unavailable()
    {
        _handler.Hang = true;

        var scores = await NewReranker(new RerankerOptions { ApiKey = "key", TimeoutSeconds = 1 }).RerankAsync("q", Docs);

        Assert.Null(scores);
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_so_a_dead_endpoint_costs_nothing()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, "{}");
        var reranker = NewReranker(new RerankerOptions { ApiKey = "key", CircuitBreakerThreshold = 2, CircuitOpenSeconds = 60 });

        await reranker.RerankAsync("q", Docs);
        await reranker.RerankAsync("q", Docs);   // second failed run trips the breaker
        var callsWhenOpen = _handler.Requests.Count;

        Assert.Null(await reranker.RerankAsync("q", Docs));
        Assert.Equal(callsWhenOpen, _handler.Requests.Count);   // no request was even attempted
    }

    [Fact]
    public async Task The_circuit_closes_again_after_its_open_period()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, "{}");
        var options = new RerankerOptions { ApiKey = "key", CircuitBreakerThreshold = 1, CircuitOpenSeconds = 60 };
        var reranker = NewReranker(options);
        await reranker.RerankAsync("q", Docs);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(61);
        _handler.Respond(HttpStatusCode.OK, @"{""results"":[{""index"":0,""relevance_score"":1},{""index"":1,""relevance_score"":1},{""index"":2,""relevance_score"":1}]}");

        Assert.NotNull(await reranker.RerankAsync("q", Docs));
    }

    [Fact]
    public async Task A_caller_cancelling_is_not_mistaken_for_the_reranker_failing()
    {
        _handler.Hang = true;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewReranker().RerankAsync("q", Docs, cts.Token));
    }

    [Fact]
    public void The_provider_name_says_whether_a_reranker_is_configured()
    {
        Assert.Equal("None", NewReranker(new RerankerOptions()).ProviderName);
        Assert.Equal("Cohere:rerank-v3.5", NewReranker().ProviderName);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _queue = new();
        private (HttpStatusCode Status, string Body)? _last;

        public bool Hang { get; set; }

        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = new();

        public void Respond(HttpStatusCode status, string body) => _queue.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            if (Hang)
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

            if (_queue.Count > 0)
                _last = _queue.Dequeue();

            var (status, text) = _last ?? (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
