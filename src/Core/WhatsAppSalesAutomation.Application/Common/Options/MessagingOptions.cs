namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "Messaging" config section. Governs the send pipeline's throughput and retry
/// behaviour. Rate limiting is deliberately simple for Phase 3: each Hangfire recurring job tick
/// sends at most <see cref="MaxSendsPerRun"/> messages, so the job's own interval times this cap is
/// the effective per-number rate limit - not a full WhatsApp-tier-aware token bucket, which is a
/// reasonable Phase 3 simplification flagged here rather than hidden.
/// </summary>
public class MessagingOptions
{
    public int MaxSendsPerRun { get; set; } = 20;

    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Backoff schedule by attempt number (1-indexed); the last value repeats past its length.</summary>
    public int[] RetryBackoffMinutes { get; set; } = { 1, 5, 15, 60, 240 };
}
