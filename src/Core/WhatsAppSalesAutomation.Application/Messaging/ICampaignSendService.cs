namespace WhatsAppSalesAutomation.Application.Messaging;

/// <summary>
/// The three moving parts of the campaign send pipeline. Each is called by a thin Hangfire job
/// wrapper in Infrastructure on its own recurring schedule; the business logic lives entirely here
/// so it is testable and runnable on demand independent of Hangfire (see the ops endpoint).
/// </summary>
public interface ICampaignSendService
{
    /// <summary>Promotes due Scheduled campaigns to Running, then sends the Initial step to Pending campaign customers.</summary>
    Task<SendRunResult> ProcessInitialSendsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends the next due follow-up to customers awaiting one.</summary>
    Task<SendRunResult> ProcessFollowUpsAsync(CancellationToken cancellationToken = default);

    /// <summary>Retries Failed sends within their attempt budget, and reconciles sends stuck at Queued
    /// past a staleness threshold (see the class remarks on the residual double-send risk this carries).</summary>
    Task<SendRunResult> RetryFailedSendsAsync(CancellationToken cancellationToken = default);
}

public record SendRunResult(int Considered, int Sent, int Failed, int Skipped)
{
    public static readonly SendRunResult Empty = new(0, 0, 0, 0);
}
