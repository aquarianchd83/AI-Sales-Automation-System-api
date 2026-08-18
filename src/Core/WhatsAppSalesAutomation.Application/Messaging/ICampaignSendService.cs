namespace WhatsAppSalesAutomation.Application.Messaging;

/// <summary>
/// The three moving parts of the campaign send pipeline. Each is called by a thin Hangfire job
/// wrapper in Infrastructure on its own recurring schedule; the business logic lives entirely here
/// so it is testable and runnable on demand independent of Hangfire (see the ops endpoint), either
/// across every eligible campaign or scoped to one via <paramref name="campaignId"/>&#160;- see the
/// per-campaign run-jobs endpoint.
/// </summary>
public interface ICampaignSendService
{
    /// <summary>Promotes due Scheduled campaigns to Running, then sends the Initial step to Pending
    /// campaign customers. <paramref name="campaignId"/> null runs across every eligible campaign;
    /// given, scopes both the promotion and the sends to that one campaign.</summary>
    Task<SendRunResult> ProcessInitialSendsAsync(Guid? campaignId = null, CancellationToken cancellationToken = default);

    /// <summary>Sends the next due follow-up to customers awaiting one. <paramref name="campaignId"/>
    /// null runs across every eligible campaign; given, scopes to that one campaign's customers.</summary>
    Task<SendRunResult> ProcessFollowUpsAsync(Guid? campaignId = null, CancellationToken cancellationToken = default);

    /// <summary>Retries Failed sends within their attempt budget, and reconciles sends stuck at Queued
    /// past a staleness threshold (see the class remarks on the residual double-send risk this
    /// carries). <paramref name="campaignId"/> null runs across every eligible message; given, scopes
    /// to messages that belong to that one campaign's customers.</summary>
    Task<SendRunResult> RetryFailedSendsAsync(Guid? campaignId = null, CancellationToken cancellationToken = default);
}

public record SendRunResult(int Considered, int Sent, int Failed, int Skipped)
{
    public static readonly SendRunResult Empty = new(0, 0, 0, 0);
}
