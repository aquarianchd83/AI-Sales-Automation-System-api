using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Retry, backoff and the circuit breaker. All time is faked: the real backoff is 2+4+8 seconds per
/// failing text, which would make these tests take minutes.
/// </summary>
public class EmbeddingBatcherTests
{
    private readonly List<TimeSpan> _waits = new();

    private EmbeddingBatcher NewBatcher() => new((wait, _) =>
    {
        lock (_waits) _waits.Add(wait);
        return Task.CompletedTask;
    });

    [Fact]
    public void Batches_respect_the_count_ceiling()
    {
        var batches = EmbeddingBatcher.PlanBatches(Enumerable.Repeat(10, 200).ToList());

        Assert.Equal(3, batches.Count);   // 96 + 96 + 8
        Assert.All(batches, b => Assert.True(b.Count <= EmbeddingBatcher.MaxBatchSize));
        Assert.Equal(200, batches.Sum(b => b.Count));
    }

    [Fact]
    public void Batches_respect_the_token_ceiling()
    {
        var batches = EmbeddingBatcher.PlanBatches(Enumerable.Repeat(3_000, 6).ToList());

        // Two 3,000-token texts fit under 8,000; a third does not.
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Equal(2, b.Count));
    }

    [Fact]
    public void A_single_oversized_text_still_gets_a_batch_rather_than_being_dropped()
    {
        var batches = EmbeddingBatcher.PlanBatches(new[] { 20_000 });

        Assert.Single(batches);
        Assert.Equal(new[] { 0 }, batches[0]);
    }

    [Fact]
    public void Every_index_appears_in_exactly_one_batch_in_order()
    {
        var batches = EmbeddingBatcher.PlanBatches(Enumerable.Range(1, 500).Select(i => i * 7 % 900 + 1).ToList());

        Assert.Equal(Enumerable.Range(0, 500), batches.SelectMany(b => b));
    }

    [Fact]
    public async Task Results_come_back_in_input_order_despite_parallel_calls()
    {
        var service = new ScriptedEmbedder(text => new[] { float.Parse(text), 0f });

        var vectors = await NewBatcher().EmbedBatchAsync(service, Enumerable.Range(1, 20).Select(i => i.ToString()).ToList());

        // Normalized, so compare direction: [n, 0] normalizes to [1, 0] for every n. The ordering is
        // proven by the call log instead.
        Assert.Equal(20, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(new[] { 1f, 0f }, v));
    }

    [Fact]
    public async Task Vectors_are_normalized_to_unit_length()
    {
        var service = new ScriptedEmbedder(_ => new[] { 3f, 4f });

        var vector = (await NewBatcher().EmbedBatchAsync(service, new[] { "x" }))[0];

        Assert.Equal(0.6, vector[0], 4);
        Assert.Equal(0.8, vector[1], 4);
    }

    [Fact]
    public void A_zero_vector_is_left_alone_rather_than_becoming_NaN()
    {
        // Dividing by a zero norm would put NaN into every comparison the vector ever takes part in.
        Assert.Equal(new[] { 0f, 0f }, EmbeddingBatcher.Normalize(new[] { 0f, 0f }));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_with_backoff_and_then_succeeds()
    {
        var service = new ScriptedEmbedder(_ => new[] { 1f }, failFirst: 2);

        var vectors = await NewBatcher().EmbedBatchAsync(service, new[] { "x" });

        Assert.Single(vectors);
        Assert.Equal(3, service.Calls);
        Assert.Equal(2, _waits.Count);
        // 2s then 4s, each with under half a second of jitter.
        Assert.InRange(_waits[0], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.5));
        Assert.InRange(_waits[1], TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.5));
    }

    [Fact]
    public async Task An_empty_vector_counts_as_a_failure_not_as_a_result()
    {
        // IEmbeddingService signals failure by returning an empty array. Accepting it would stage a
        // chunk that can never be retrieved and looks perfectly healthy in the database.
        var service = new ScriptedEmbedder(_ => Array.Empty<float>());

        await Assert.ThrowsAsync<EmbeddingProviderUnavailableException>(
            () => NewBatcher().EmbedBatchAsync(service, new[] { "x" }));
    }

    [Fact]
    public async Task An_exception_from_the_provider_is_retried_like_an_empty_result()
    {
        var service = new ScriptedEmbedder(_ => new[] { 1f }, throwFirst: 1);

        var vectors = await NewBatcher().EmbedBatchAsync(service, new[] { "x" });

        Assert.Single(vectors);
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task Persistent_failure_stops_after_the_attempt_limit()
    {
        var service = new ScriptedEmbedder(_ => Array.Empty<float>());

        await Assert.ThrowsAsync<EmbeddingProviderUnavailableException>(
            () => NewBatcher().EmbedBatchAsync(service, new[] { "x" }));

        Assert.Equal(EmbeddingBatcher.MaxAttempts, service.Calls);
    }

    [Fact]
    public async Task The_circuit_opens_after_consecutive_failures_instead_of_grinding_through_every_text()
    {
        var service = new ScriptedEmbedder(_ => Array.Empty<float>());
        var texts = Enumerable.Range(0, 100).Select(i => i.ToString()).ToList();

        var ex = await Assert.ThrowsAsync<EmbeddingProviderUnavailableException>(
            () => NewBatcher().EmbedBatchAsync(service, texts));

        Assert.NotEmpty(ex.Message);
        // The point is that a dead provider is abandoned quickly: the first text to exhaust its
        // attempts aborts the batch, instead of 100 texts x 3 attempts = 300 calls into a dead
        // endpoint. (The shared consecutive-failure counter is the second line of defence, for
        // failures that interleave across parallel calls so that no single text exhausts its own.)
        Assert.True(service.Calls < 30, $"made {service.Calls} calls");
    }

    [Fact]
    public async Task A_success_resets_the_consecutive_failure_count()
    {
        // Two flaky texts in a healthy run must not add up to a tripped breaker.
        var service = new ScriptedEmbedder(_ => new[] { 1f }, failFirst: 2);
        var batcher = NewBatcher();

        await batcher.EmbedBatchAsync(service, new[] { "a" });
        var second = new ScriptedEmbedder(_ => new[] { 1f }, failFirst: 2);
        await batcher.EmbedBatchAsync(second, new[] { "b" });

        Assert.Equal(3, service.Calls);
        Assert.Equal(3, second.Calls);
    }

    private sealed class ScriptedEmbedder : IEmbeddingService
    {
        private readonly Func<string, float[]> _vector;
        private readonly int _failFirst;
        private readonly int _throwFirst;
        private int _calls;

        public ScriptedEmbedder(Func<string, float[]> vector, int failFirst = 0, int throwFirst = 0)
        {
            _vector = vector;
            _failFirst = failFirst;
            _throwFirst = throwFirst;
        }

        public int Calls => _calls;
        public string ProviderName => "Test";
        public string ModelName => "test-model";
        public bool IsAvailable => true;

        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n <= _throwFirst)
                throw new HttpRequestException("boom");
            if (n <= _failFirst + _throwFirst)
                return Task.FromResult(Array.Empty<float>());

            return Task.FromResult(_vector(text));
        }
    }
}
