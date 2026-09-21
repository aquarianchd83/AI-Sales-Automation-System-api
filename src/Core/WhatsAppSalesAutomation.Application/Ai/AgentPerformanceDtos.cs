namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>
/// What the AI sales agent actually did over a window, and where it is going wrong.
///
/// Four questions, deliberately answered together rather than as four endpoints: they are read as one
/// story. A falling containment rate means little until you see that one qualification question stopped
/// working, or that UngroundedNumber started firing the week the price list changed.
/// </summary>
public record AgentPerformanceReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    AgentTurnStatsDto Turns,
    IReadOnlyList<QualificationFieldStatsDto> Fields,
    IReadOnlyList<ValidationFailureStatsDto> ValidationFailures,
    LeadOutcomeStatsDto Leads);

/// <summary>Volume and cost, and the one number that says whether the agent is earning its keep:
/// containment, the share of turns it handled without a human.</summary>
public record AgentTurnStatsDto(
    int TotalTurns,
    int RepliedTurns,
    int EscalatedTurns,

    /// <summary>Replied / total. Not a target to maximise - an agent that never escalates is one that
    /// answers questions it should have handed over.</summary>
    double ContainmentRate,

    double AverageConfidence,

    /// <summary>Turns where a blocking check stopped the reply. Distinct from escalations: most
    /// escalations are a judgement call about the customer, these are the agent's own output being
    /// unusable.</summary>
    int BlockedReplies,

    int AverageLatencyMs,
    long PromptTokens,
    long CompletionTokens);

/// <summary>
/// Per qualification field: is this question working?
///
/// <para><paramref name="AskEffectiveness"/> is the one to read first. A field asked in 80
/// conversations and answered in 12 of them is a badly phrased question, not a stubborn customer -
/// and that distinction is the whole reason this table exists.</para>
/// </summary>
public record QualificationFieldStatsDto(
    string FieldKey,
    string DisplayName,
    bool IsRequired,
    bool IsActive,

    /// <summary>Every time the agent reported asking for this field, including repeat asks in the
    /// same conversation.</summary>
    int TimesAsked,

    int ConversationsAsked,

    /// <summary>Conversations where a value arrived at all - including ones where the customer
    /// volunteered it without being asked, which is why this can exceed
    /// <paramref name="ConversationsAsked"/>.</summary>
    int ConversationsCaptured,

    /// <summary>Conversations where the agent asked and a value followed. The numerator of
    /// <paramref name="AskEffectiveness"/>.</summary>
    int ConversationsCapturedAfterAsk,

    /// <summary>CapturedAfterAsk / Asked, 0 when never asked. What the question is worth.</summary>
    double AskEffectiveness,

    /// <summary>Turns from the first ask to the answer, averaged over the conversations where both
    /// happened. Null when the field was never captured after an ask. A field that takes four turns
    /// is costing a conversation its patience.</summary>
    double? AverageTurnsToCapture);

/// <summary>
/// One output check, and how often it fired.
///
/// Not a scoreboard for the model. A spike in UngroundedNumber usually means the knowledge base is
/// missing the thing customers keep asking about; a spike in UnofferedField means the prompt and the
/// planner disagree about what was on offer. The code names where to look.
/// </summary>
public record ValidationFailureStatsDto(
    string Code,

    /// <summary>Whether this check stops the reply. Advisory failures are worth watching but do not
    /// cost the customer an answer.</summary>
    bool Blocking,

    int Occurrences,
    int AffectedConversations,

    /// <summary>Occurrences / total turns in the window. A rate, because raw counts rise with volume
    /// and hide the thing you are looking for.</summary>
    double ShareOfTurns);

/// <summary>
/// Where the scoring ends up, and whether being called hot means anything.
///
/// <paramref name="HotConversionRate"/> against <paramref name="OtherConversionRate"/> is the test of
/// the whole scoring configuration. If hot leads do not close better than the rest, the rules are
/// measuring something that is not buying intent, and the fix is in the rules rather than in the
/// sales team.
/// </summary>
public record LeadOutcomeStatsDto(
    int TotalLeads,
    int Hot,
    int Warm,
    int Cold,

    /// <summary>Leads the hot-lead rules fired on, which is not the same as leads currently scoring
    /// in the hot band - a lead is stamped hot once and keeps the stamp.</summary>
    int HotLeadsDetected,

    int HotLeadsWon,
    double HotConversionRate,
    int OtherLeadsWon,
    double OtherConversionRate);
