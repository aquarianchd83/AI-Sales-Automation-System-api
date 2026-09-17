using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

/// <summary>
/// What one lead discovery run cost and produced for one tenant, written when the run finishes. The
/// tenant-facing record of discovery spend: TenantJobSchedule.LastRunSummary holds only the newest run and is
/// visible to a PlatformSuperAdmin alone, so it cannot answer "what has this cost me this month".
///
/// Rows are only written for runs that actually researched - a run skipped for a missing profile or a missing
/// API key spends nothing and records nothing. A simulated run (see SimulatedLeadDiscoveryAgent) does write a
/// row, with zeros throughout, so a test run is visible as having happened and as having been free.
/// </summary>
public class LeadDiscoveryRun : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public DateTime RanAtUtc { get; set; }

    /// <summary>The model that did the research, or "Simulated". Kept per run because the platform's model
    /// setting changes, and an old run's cost was calculated at that model's rates.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Agent rounds this run made - each one is a full round of web research.</summary>
    public int Rounds { get; set; }

    public int CandidatesConsidered { get; set; }

    public int LeadsSaved { get; set; }

    public int Duplicates { get; set; }

    public int Rejected { get; set; }

    // ---- Usage as the API reported it, summed over every request the run made. ----

    /// <summary>Input tokens that were neither read from nor written to the prompt cache.</summary>
    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int CacheReadTokens { get; set; }

    public int CacheWriteTokens { get; set; }

    public int WebSearches { get; set; }

    public int WebFetches { get; set; }

    /// <summary>
    /// USD, from the usage above priced at the configured rates (LeadDiscoveryPricingOptions) for
    /// <see cref="Model"/> - see LeadDiscoveryCost. An estimate, not an invoice: the platform's Anthropic bill
    /// is the authority, and configured rates can lag a provider price change. Stored per run rather than
    /// recomputed on read, so a later rate change does not silently rewrite history - the same
    /// snapshot-at-the-time reasoning as Payment.AmountCents.
    /// </summary>
    public decimal EstimatedCostUsd { get; set; }
}
