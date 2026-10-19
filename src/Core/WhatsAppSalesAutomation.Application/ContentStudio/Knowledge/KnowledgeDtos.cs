using System.Text.Json.Serialization;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;

/// <summary>Where a requirement stands for one tenant. Written as a word in the API.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementState
{
    /// <summary>Enough verified, current facts (with proof where proof is needed).</summary>
    Met,

    /// <summary>The tenant has told us, but nobody has confirmed it yet.</summary>
    NeedsConfirmation,

    /// <summary>The tenant has told us, but not how they know it, and this requirement needs proof.</summary>
    NeedsEvidence,

    /// <summary>Was confirmed, but is older than the rule allows.</summary>
    Outdated,

    /// <summary>Was confirmed, but its own date has passed.</summary>
    Expired,

    Missing,

    /// <summary>The tenant said "we don't need this".</summary>
    Skipped
}

/// <summary>Why a gap is open: the reason a follow-up is being asked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeGapReason
{
    Missing,

    /// <summary>The tenant answered "I don't know" to the question that would have supplied it.</summary>
    Unknown,

    /// <summary>The tenant answered "I need help" to it.</summary>
    NeedsHelp,
    NeedsConfirmation,
    NeedsEvidence,
    Outdated,
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentReadinessStatus
{
    Ready,

    /// <summary>Everything critical has been told to us; it only waits for a person to confirm or document it.</summary>
    NeedsConfirmation,

    NotReady
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeGapState
{
    Open,
    Deferred
}

/// <summary>One requirement as it counted towards one kind of content - the "why" behind a score.</summary>
public record RequirementResultDto(
    string Key,
    string Label,
    double Weight,
    bool Critical,
    double Fulfilment,
    double Points,
    RequirementState State,
    string Explanation,
    IReadOnlyList<Guid> FactIds);

public record ContentTypeReadinessDto(
    string Key,
    string Label,
    string? Description,
    int Score,
    int MinScore,
    ContentReadinessStatus Status,
    string Summary,
    IReadOnlyList<RequirementResultDto> Requirements);

/// <summary>One thing the studio still needs to know, phrased as a question the tenant can answer in a sentence.</summary>
public record KnowledgeGapDto(
    string Key,
    string Label,
    KnowledgeGapReason Reason,
    KnowledgeGapState State,
    DateTime? DeferredUntilUtc,
    string Question,
    string Why,
    string? Example,
    bool RequiresEvidence,
    bool Skippable,
    IReadOnlyList<string> NeededFor,
    IReadOnlyList<Guid> FactsToConfirm,
    IReadOnlyList<Guid> FactsInvolved);

public record KnowledgeFactCountsDto(int Verified, int NeedsChecking, int Expired, int Retired);

public record KnowledgeReadinessDto(
    int OverallScore,
    IReadOnlyList<ContentTypeReadinessDto> ContentTypes,
    IReadOnlyList<KnowledgeGapDto> Gaps,
    KnowledgeFactCountsDto Facts,
    string? RulesWarning,
    DateTime EvaluatedAtUtc);

/// <summary>The tenant's answer to a follow-up question. <see cref="Evidence"/> is how they know it is true; some requirements need it.</summary>
public record AnswerGapRequest(string Answer, string? Evidence, string? Context);

public record DeferGapRequest(DateTime? Until);

// ----- claims ----------------------------------------------------------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaimVerdict
{
    /// <summary>Matches one or more verified, current facts.</summary>
    Supported,

    /// <summary>The tenant has said something similar, but nobody has verified it.</summary>
    Unverified,

    /// <summary>Nothing the tenant has told us supports it.</summary>
    Unsupported,
}

public record CheckClaimsRequest(IReadOnlyList<string> Claims);

public record ClaimCheckDto(string Claim, ClaimVerdict Verdict, string Reason, IReadOnlyList<Guid> SupportingFactIds, bool IsRisky);

public record ClaimCheckResultDto(IReadOnlyList<ClaimCheckDto> Claims, int Supported, int Flagged);

// ----- retrieval -------------------------------------------------------------------------------------------------------------------------

/// <summary>A fact as the AI side sees it: only what it may say, plus the id to cite.</summary>
public record GroundingFactDto(Guid Id, string Category, string? Subject, string Statement, string? Context, double Score);

public record GroundingResultDto(IReadOnlyList<GroundingFactDto> Facts, string PromptBlock);
