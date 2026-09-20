namespace WhatsAppSalesAutomation.Application.Handoffs;

/// <summary>
/// The briefing an agent gets when the AI steps back.
///
/// Before this, a handoff carried one line: the intent and a confidence percentage. Whoever picked it
/// up had to read the whole conversation to find out what the customer wanted, what had already been
/// asked, and why the AI gave up - work the system had already done and then thrown away.
///
/// Assembled once, when the handoff is raised, and stored. Not recomputed when someone opens the
/// screen: the lead keeps changing afterwards, and a summary regenerated on view would quietly
/// describe a different situation than the one that triggered the escalation. It is refreshed if a
/// later turn escalates again on the same open handoff, because then the situation genuinely has
/// changed and "what the agent knew" should mean the most recent moment, not the first.
/// </summary>
public record HandoffSummary(
    string CustomerName,
    string CustomerPhoneNumberE164,

    /// <summary>The AI's running summary of the conversation - what the customer is actually after.</summary>
    string? Requirement,

    /// <summary>What the customer has told us, newest first.</summary>
    IReadOnlyList<HandoffQualificationItem> Qualification,

    /// <summary>Configured fields still unanswered, in the order the agent would have asked them.
    /// Shown because it tells the person taking over what to cover, which is more useful than the
    /// list of what is already known.</summary>
    IReadOnlyList<string> StillUnknown,

    int ScoreNumeric,

    /// <summary>Hot / Warm / Cold.</summary>
    string Temperature,

    bool IsHot,
    string? HotReason,

    /// <summary>Which rules and field weights produced the score. A score nobody can take apart is one
    /// the sales team learns to ignore, so the workings travel with it.</summary>
    IReadOnlyList<HandoffScoreLine> ScoreBreakdown,

    string? DetectedIntent,
    string TriggerReason,

    /// <summary>The AI's own one-sentence conclusion, if it gave one.</summary>
    string? AgentNote,

    /// <summary>Set when the reply was blocked by validation rather than by a policy or confidence
    /// rule - names which checks failed, so an agent seeing a suppressed reply knows why.</summary>
    string? BlockedReason,

    string? LastCustomerMessage,
    int MessageCount,
    DateTime ConversationStartedAt,
    DateTime EscalatedAt);

/// <summary>One captured answer. <paramref name="EnteredByHuman"/> distinguishes what a colleague
/// typed from what the AI extracted - worth knowing before acting on it.</summary>
public record HandoffQualificationItem(
    string DisplayName,
    string RawValue,
    bool EnteredByHuman,
    DateTime CapturedAt);

/// <summary>One contribution to the score, already stripped of its storage prefix.</summary>
public record HandoffScoreLine(string Label, int Points);
