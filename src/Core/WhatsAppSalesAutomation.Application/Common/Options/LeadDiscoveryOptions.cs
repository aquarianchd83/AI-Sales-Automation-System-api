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
}
