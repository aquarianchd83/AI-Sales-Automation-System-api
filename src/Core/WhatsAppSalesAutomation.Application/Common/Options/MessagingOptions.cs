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

    /// <summary>Backoff schedule by attempt number (1-indexed); the last value repeats past its length.
    /// Must default to an empty array, not the real {1,5,15,60,240} schedule - ConfigurationBinder
    /// appends config-bound array items onto an already-non-null array property rather than replacing
    /// it, so a non-empty default here would come back from IOptionsSnapshot&lt;MessagingOptions&gt;
    /// with every value duplicated. appsettings.json's own "Messaging:RetryBackoffMinutes" array is
    /// what actually supplies the real default for a fresh install with no DB override.</summary>
    public int[] RetryBackoffMinutes { get; set; } = Array.Empty<int>();

    /// <summary>
    /// WhatsApp's customer service window (Phase 4): free-form text replies are only allowed within
    /// this many hours of the customer's last inbound message; an approved template is required
    /// outside it. 24 is Meta's real rule - configurable only so it is not a hardcoded magic number
    /// buried in ConversationService.
    /// </summary>
    public int CustomerServiceWindowHours { get; set; } = 24;

    /// <summary>Most template messages a tenant may send in any rolling 24 hours. A starting value
    /// (new tenants run on one shared WhatsApp number, so volume is kept low until quality is proven),
    /// to be tuned from real data and raised per tenant as their quality rating earns it.</summary>
    public int DailyTemplateCap { get; set; } = 200;

    /// <summary>Most promotional (Marketing-category) templates one contact may receive within
    /// <see cref="MarketingWindowDays"/>. A starting value: repeated promotions are what drive blocks
    /// and reports, which lower the shared number's quality rating.</summary>
    public int MaxMarketingPerContactPerWindow { get; set; } = 2;

    /// <summary>The window, in days, that <see cref="MaxMarketingPerContactPerWindow"/> is counted over.</summary>
    public int MarketingWindowDays { get; set; } = 7;

    /// <summary>Days of unbroken Green quality needed to move up one step in <see cref="CapStepUps"/>. A starting value.</summary>
    public int CapStepUpAfterGreenDays { get; set; } = 7;

    /// <summary>The daily caps a tenant earns, in order, after each <see cref="CapStepUpAfterGreenDays"/> of Green
    /// quality; the first step is <see cref="DailyTemplateCap"/> itself. Empty means no step-ups. Must default to
    /// empty, not the real values: configuration binding appends to a non-empty array default instead of replacing
    /// it (see <see cref="RetryBackoffMinutes"/>). appsettings.json supplies the real starting steps.</summary>
    public int[] CapStepUps { get; set; } = Array.Empty<int>();
}
