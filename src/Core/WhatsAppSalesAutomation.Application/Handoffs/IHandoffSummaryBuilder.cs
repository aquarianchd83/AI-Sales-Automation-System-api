using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Handoffs;

/// <summary>
/// Assembles the briefing that travels with a handoff.
///
/// Built from stored state rather than from the model's output. Everything in the packet is already
/// known for certain - what the customer said, what was captured, what the score is and why - so
/// asking the model to summarise it would only add something else to verify.
/// </summary>
public interface IHandoffSummaryBuilder
{
    Task<HandoffSummary> BuildAsync(
        Guid leadId, Guid conversationId, HandoffTurnContext turn, CancellationToken cancellationToken = default);
}

/// <summary>
/// The parts of the escalating turn that are not yet in the database.
///
/// Score contributions are passed in rather than read back because at escalation time this turn's
/// contributions are staged on the change tracker and not yet committed - EF does not flush before a
/// query, so the builder would otherwise produce a breakdown that is one turn stale for exactly the
/// turn it is describing.
///
/// Deliberately all primitives and Handoffs-local types: the builder has no reason to know about the
/// AI or Leads namespaces, and the Ai namespace already depends on this one.
/// </summary>
public record HandoffTurnContext(
    HandoffTriggerReason TriggerReason,
    string? DetectedIntent,
    string? AgentNote,
    string? BlockedReason,
    int ScoreNumeric,
    string ScoreBand,
    bool IsHot,
    string? HotReason,
    IReadOnlyList<HandoffScoreLine> NewScoreLines);
