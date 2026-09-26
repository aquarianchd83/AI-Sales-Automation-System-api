namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "LeadDiscovery" config section. How a discovery run is paced - which model does the
/// research, and how many searches and fetches it may use, is Infrastructure's LeadDiscoveryAgentSettings
/// ("LeadDiscovery:Agent"), the same split as AiOptions vs AiProviderSettings.
/// </summary>
public class LeadDiscoveryOptions
{
    /// <summary>The most agent requests one run makes while its batch is not yet full. Each round is a full
    /// round of web research, so this is the main bound on a run's cost.</summary>
    public int MaxRounds { get; set; } = 3;

    /// <summary>The most candidates asked for in one round. Larger batches are filled over several rounds
    /// rather than one very long agent turn.</summary>
    public int MaxCandidatesPerRound { get; set; } = 20;

    /// <summary>How many previously discovered businesses are listed to the agent as already known.</summary>
    public int MaxKnownBusinessesInPrompt { get; set; } = 200;

    /// <summary>How long one acquisition or renewal of the tenant+profile lock is valid for. Must comfortably
    /// exceed <see cref="LockHeartbeatSeconds"/>, so one missed heartbeat does not lose the lock.</summary>
    public int LockLeaseSeconds { get; set; } = 600;

    /// <summary>How often the background heartbeat renews the lock while an execution runs. 0 disables the
    /// heartbeat, leaving renewal to the processing checkpoints.</summary>
    public int LockHeartbeatSeconds { get; set; } = 120;

    /// <summary>A processing checkpoint renews inline when less than this is left on the lease.</summary>
    public int LockRenewWhenRemainingSeconds { get; set; } = 240;

    /// <summary>How many automatic retries (on subsequent scheduled runs) an execution with unfinished work
    /// gets. A manual retry from Lead Discovery History is always allowed.</summary>
    public int MaxRetryAttempts { get; set; } = 3;
}
