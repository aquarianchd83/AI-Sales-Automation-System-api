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

    /// <summary>
    /// WhatsApp's customer service window (Phase 4): free-form text replies are only allowed within
    /// this many hours of the customer's last inbound message; an approved template is required
    /// outside it. 24 is Meta's real rule - configurable only so it is not a hardcoded magic number
    /// buried in ConversationService.
    /// </summary>
    public int CustomerServiceWindowHours { get; set; } = 24;
}
