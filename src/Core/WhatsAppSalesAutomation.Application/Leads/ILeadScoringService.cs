namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>
/// Works out what a lead is currently worth, from the tenant's configured rules and field weights.
///
/// Deterministic and evaluated in C#, never by the model - see <see cref="ILeadScoringAdminService"/>
/// for why. The model reports what happened in a turn; this decides what that is worth.
/// </summary>
public interface ILeadScoringService
{
    /// <summary>
    /// Records whatever this turn newly earned, then recomputes the lead's score from every
    /// contribution on record. Writes <c>Lead.ScoreNumeric</c>/<c>Score</c> and the hot-lead fields,
    /// but does NOT call SaveChanges - the caller owns the unit of work, so one AI turn stays a single
    /// transaction rather than several that can half-apply.
    /// </summary>
    Task<LeadScoreResult> RecomputeAsync(
        Guid leadId, ScoringSignals signals, CancellationToken cancellationToken = default);
}

/// <summary>
/// What happened in this turn that rules can match on. Assembled by the caller from the AI's output
/// after it has been validated - never straight from the model.
///
/// Every member is optional because the callers arrive in stages: today's lead path has the detected
/// intent and nothing else, while the reworked orchestrator will supply the rest. A signal that is
/// absent simply means the rules keyed on it do not fire, which is the correct reading of "we do not
/// know" - not a reason to guess.
/// </summary>
public record ScoringSignals(
    string? DetectedIntent = null,
    bool BuyingIntentDetected = false,
    string? InboundMessageText = null,
    Guid? AiInteractionId = null);

public record LeadScoreResult(
    int ScoreNumeric,
    /// <summary>Sum before clamping. Surfaced so a tenant whose rules add up past 100 can see that,
    /// rather than wondering why every good lead reads exactly 100.</summary>
    int RawTotal,
    string Band,
    bool IsHot,
    string? HotReason,
    IReadOnlyList<LeadScoreContributionDto> NewContributions);
