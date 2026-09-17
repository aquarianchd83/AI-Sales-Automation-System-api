namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Searches the web for businesses matching one discovery request and reports what it found together
/// with the evidence it gathered. Implemented in Infrastructure with Claude's server-side web search and
/// web fetch tools, using the calling tenant's own Anthropic API key.
///
/// The agent only proposes candidates. Deciding which ones qualify - verifying phone numbers and emails
/// against <see cref="LeadDiscoveryEvidence"/>, applying the tenant's rules, removing duplicates - is
/// LeadDiscovery.LeadQualification's job in the Application layer, so none of those guarantees rests on the
/// model following its instructions.
/// </summary>
public interface ILeadDiscoveryAgent
{
    Task<LeadDiscoveryAgentResult> DiscoverAsync(LeadDiscoveryAgentRequest request, CancellationToken cancellationToken = default);
}

/// <param name="MaxCandidates">The most candidates the agent should return for this request.</param>
/// <param name="KnownBusinesses">"Name, City" of businesses already discovered, so the agent does not spend
/// its searches finding them again. A hint only - duplicates are still removed after the fact.</param>
public record LeadDiscoveryAgentRequest(
    string TargetBusinessType,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Locations,
    int MaxCandidates,
    IReadOnlyList<string> RequiredFields,
    bool PhoneRequired,
    bool EmailRequired,
    bool IndependentBusiness,
    int MinimumLeadScore,
    IReadOnlyList<string> AdditionalCriteria,
    IReadOnlyList<string> KnownBusinesses);

/// <summary>One business as the agent reported it - unverified, and possibly missing anything.</summary>
public record DiscoveredBusinessCandidate(
    string BusinessName,
    string BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? State,
    string? Phone,
    string? PhoneSourceUrl,
    string? Email,
    string? Website,
    string SourceUrl,
    bool? IsIndependentBusiness,
    bool? IsPermanentlyClosed,
    int LeadScore,
    string? ScoreRationale);

/// <summary>A page the agent retrieved, as text. PDFs and other binary content are not included.</summary>
public record FetchedPage(string Url, string Text);

/// <summary>What the agent actually looked at: every URL that came back from a search or a fetch, and the
/// text of every page it fetched.</summary>
public record LeadDiscoveryEvidence(IReadOnlyList<string> SeenUrls, IReadOnlyList<FetchedPage> FetchedPages)
{
    public static readonly LeadDiscoveryEvidence Empty = new(Array.Empty<string>(), Array.Empty<FetchedPage>());
}

/// <summary>What discovery consumed, summed over every API response. The cost of a run: tokens at the model's
/// rates (cache reads are cheaper and cache writes dearer than plain input), plus a per-search charge for
/// web searches. Fetched page text is billed as input tokens, not per fetch.</summary>
/// <param name="InputTokens">Input tokens not read from or written to the prompt cache.</param>
public record LeadDiscoveryUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens,
    int WebSearches,
    int WebFetches)
{
    public static readonly LeadDiscoveryUsage None = new(0, 0, 0, 0, 0, 0);

    public static LeadDiscoveryUsage operator +(LeadDiscoveryUsage a, LeadDiscoveryUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadInputTokens + b.CacheReadInputTokens,
        a.CacheCreationInputTokens + b.CacheCreationInputTokens,
        a.WebSearches + b.WebSearches,
        a.WebFetches + b.WebFetches);
}

/// <param name="NotConfiguredReason">Set when <paramref name="IsConfigured"/> is false - discovery has no
/// credentials to run with, which is a reason to skip the run, not a failure.</param>
/// <param name="Model">What did the research ("Simulated" for the no-cost agent). Recorded per run, because
/// the usage above is only priceable at the rates of the model that produced it.</param>
public record LeadDiscoveryAgentResult(
    bool IsConfigured,
    string? NotConfiguredReason,
    IReadOnlyList<DiscoveredBusinessCandidate> Candidates,
    LeadDiscoveryEvidence Evidence,
    LeadDiscoveryUsage Usage,
    string Model)
{
    public static LeadDiscoveryAgentResult NotConfigured(string reason) =>
        new(false, reason, Array.Empty<DiscoveredBusinessCandidate>(), LeadDiscoveryEvidence.Empty, LeadDiscoveryUsage.None, string.Empty);
}
