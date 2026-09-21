using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>Thrown when the provider has failed enough times in a row that continuing would only burn
/// time and quota. The ingestion job treats it as a resumable failure, not a content problem.</summary>
public sealed class EmbeddingProviderUnavailableException : Exception
{
    public EmbeddingProviderUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Embeds text in bounded batches, with retry, bounded parallelism and a circuit breaker (§I.4).
///
/// IEmbeddingService embeds one text per call and signals failure by returning an empty array rather
/// than throwing (see its GetEmbeddingAsync contract), so "empty" is treated as a failed attempt
/// exactly like an exception. Treating it as a valid zero-length vector would stage a chunk that can
/// never be retrieved and looks perfectly fine in the database.
///
/// Parallelism is over provider calls only. Nothing here touches a DbContext, which is not
/// thread-safe - the caller stages results on its own thread.
/// </summary>
public sealed class EmbeddingBatcher
{
    /// <summary>Texts per batch (96, the practical OpenAI sweet spot).</summary>
    public const int MaxBatchSize = 96;

    /// <summary>Estimated input tokens per batch, kept well under provider limits.</summary>
    public const int MaxBatchTokens = 8_000;

    public const int MaxParallelCalls = 3;

    public const int MaxAttempts = 3;

    /// <summary>Consecutive failed attempts, across all parallel calls, before giving up.</summary>
    public const int CircuitBreakerThreshold = 5;

    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)
    };

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _jitter = new();

    /// <param name="delay">Injected so tests do not actually wait 2+4+8 seconds per failure.</param>
    public EmbeddingBatcher(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Splits texts into batches respecting both the count and the token ceiling. A single
    /// text over the token ceiling still gets a batch to itself rather than being dropped.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> PlanBatches(IReadOnlyList<int> tokenCounts)
    {
        var batches = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        var currentTokens = 0;

        for (var i = 0; i < tokenCounts.Count; i++)
        {
            var wouldOverflow = current.Count >= MaxBatchSize || (current.Count > 0 && currentTokens + tokenCounts[i] > MaxBatchTokens);
            if (wouldOverflow)
            {
                batches.Add(current);
                current = new List<int>();
                currentTokens = 0;
            }

            current.Add(i);
            currentTokens += tokenCounts[i];
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    private int _consecutiveFailures;

    /// <summary>Embeds one batch, returning L2-normalized vectors in input order.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEmbeddingService service, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new float[texts.Count][];
        using var gate = new SemaphoreSlim(MaxParallelCalls);

        // The first text to give up cancels every other one. Without this, a dead provider is still
        // called once for each of the remaining texts - the whole batch, one wasted call at a time -
        // because Task.WhenAll waits for every task rather than stopping at the first fault.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = texts.Select(async (text, index) =>
        {
            try
            {
                await gate.WaitAsync(abort.Token);
                try
                {
                    results[index] = Normalize(await EmbedOneAsync(service, text, abort.Token));
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (EmbeddingProviderUnavailableException)
            {
                abort.Cancel();
                throw;
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch when (tasks.Any(t => t.Exception?.InnerException is EmbeddingProviderUnavailableException))
        {
            // Report the real cause, not whichever cancellation happened to be observed first.
            throw tasks.Select(t => t.Exception?.InnerException).OfType<EmbeddingProviderUnavailableException>().First();
        }

        return results;
    }

    private async Task<float[]> EmbedOneAsync(IEmbeddingService service, string text, CancellationToken cancellationToken)
    {
        Exception? last = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Already open: do not spend a call proving it again.
            if (Volatile.Read(ref _consecutiveFailures) >= CircuitBreakerThreshold)
                throw new EmbeddingProviderUnavailableException(
                    $"{service.ProviderName} failed {CircuitBreakerThreshold} times in a row; stopping so the job can resume later.");

            try
            {
                var vector = await service.GetEmbeddingAsync(text, cancellationToken);
                if (vector.Length > 0)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return vector;
                }

                last = null;   // an empty vector is the provider's "I failed" signal, not an exception
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }

            if (Interlocked.Increment(ref _consecutiveFailures) >= CircuitBreakerThreshold)
            {
                throw new EmbeddingProviderUnavailableException(
                    $"{service.ProviderName} failed {CircuitBreakerThreshold} times in a row; stopping so the job can resume later.", last);
            }

            if (attempt < MaxAttempts - 1)
            {
                // Jitter, so parallel calls that failed together do not all retry in the same instant.
                var wait = Backoff[attempt] + TimeSpan.FromMilliseconds(_jitter.Next(0, 500));
                await _delay(wait, cancellationToken);
            }
        }

        throw new EmbeddingProviderUnavailableException(
            $"{service.ProviderName} returned no embedding after {MaxAttempts} attempts.", last);
    }

    /// <summary>Scales to unit length, so cosine similarity is a plain dot product (§I.5). A zero
    /// vector is returned unchanged rather than dividing by zero into NaNs, which would poison every
    /// comparison it later appears in.</summary>
    public static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
            sum += (double)v * v;

        if (sum <= 0)
            return vector;

        var norm = (float)Math.Sqrt(sum);
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            result[i] = vector[i] / norm;

        return result;
    }
}
