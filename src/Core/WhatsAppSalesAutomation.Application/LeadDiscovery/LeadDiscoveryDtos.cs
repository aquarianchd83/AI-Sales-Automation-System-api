namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

public static class LeadDiscoveryLimits
{
    /// <summary>Upper bound on a profile's own batch size, whatever the plan allows - a run this large already
    /// takes many rounds of web research.</summary>
    public const int MaxBatchSize = 200;

    public const int MaxKeywords = 25;
    public const int MaxLocations = 25;
    public const int MaxAdditionalCriteria = 20;

    public const int TargetBusinessType = 200;
    public const int Keyword = 100;
    public const int Location = 200;
    public const int Criterion = 500;

    // DiscoveredLead column lengths.
    public const int BusinessName = 300;
    public const int BusinessType = 200;
    public const int ContactPerson = 200;
    public const int Address = 500;
    public const int City = 100;
    public const int State = 100;
    public const int Phone = 50;
    public const int PhoneE164 = 20;
    public const int PhoneKey = 15;
    public const int Email = 256;
    public const int Url = 2000;
    public const int ScoreRationale = 1000;

    /// <summary>LeadDiscoveryRun.Model.</summary>
    public const int Model = 100;

    /// <summary>Keeps the dedupe key indexes under SQL Server's 900-byte index key limit.</summary>
    public const int DedupeKey = 450;
}

/// <param name="PlanMaxBatchSize">The tenant's plan cap on new leads per run; null when no plan applies.</param>
public record LeadDiscoveryProfileDto(
    bool IsEnabled,
    string TargetBusinessType,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Locations,
    int BatchSize,
    int? PlanMaxBatchSize,
    IReadOnlyList<string> RequiredFields,
    bool PhoneRequired,
    bool EmailRequired,
    bool IndependentBusiness,
    int MinimumLeadScore,
    IReadOnlyList<string> AdditionalCriteria,
    DateTime? UpdatedAt);

/// <summary>Body of PUT lead-discovery/profile. Replaces the whole profile. RequiredFields are
/// LeadDiscoveryFields names, in any case.</summary>
public record SaveLeadDiscoveryProfileRequest(
    bool IsEnabled,
    string TargetBusinessType,
    IReadOnlyList<string>? Keywords,
    IReadOnlyList<string>? Locations,
    int BatchSize,
    IReadOnlyList<string>? RequiredFields = null,
    bool PhoneRequired = true,
    bool EmailRequired = false,
    bool IndependentBusiness = false,
    int MinimumLeadScore = 60,
    IReadOnlyList<string>? AdditionalCriteria = null);

/// <summary>What one lead discovery run cost and produced. <paramref name="EstimatedCostUsd"/> is the figure
/// the run was priced at when it ran; <paramref name="EstimatedCostLocal"/> is that converted to the tenant's
/// own currency for display, the same treatment PlanDto gives a plan price.</summary>
public record LeadDiscoveryRunDto(
    Guid Id,
    DateTime RanAtUtc,
    string Model,
    int Rounds,
    int CandidatesConsidered,
    int LeadsSaved,
    int Duplicates,
    int Rejected,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    int WebSearches,
    int WebFetches,
    decimal EstimatedCostUsd,
    decimal EstimatedCostLocal);

/// <param name="CostPerLeadUsd">Zero when the period saved no leads.</param>
public record LeadDiscoverySpendPeriodDto(
    int Runs,
    int LeadsSaved,
    decimal EstimatedCostUsd,
    decimal EstimatedCostLocal,
    decimal CostPerLeadUsd);

/// <summary>The tenant's lead discovery spend. An estimate priced from configured list rates when each run
/// happened - close, but the platform's own provider bill is the authority.</summary>
public record LeadDiscoverySpendDto(
    string CurrencyCode,
    string CurrencySymbol,
    LeadDiscoverySpendPeriodDto CurrentMonth,
    LeadDiscoverySpendPeriodDto AllTime,
    DateTime? LastRunAtUtc);

/// <summary>One discovered lead, in the output shape lead discovery promises.</summary>
public record DiscoveredLeadDto(
    Guid Id,
    string BusinessName,
    string BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? State,
    string? Phone,
    string? Email,
    string? Website,
    string SourceUrl,
    bool PhoneVerified,
    string? PhoneSourceUrl,
    string QualificationStatus,
    int LeadScore,
    string? ScoreRationale,
    /// <summary>The CRM customer created for this business, or null when it had no usable phone number.</summary>
    Guid? CustomerId,
    DateTime DiscoveredAt);
