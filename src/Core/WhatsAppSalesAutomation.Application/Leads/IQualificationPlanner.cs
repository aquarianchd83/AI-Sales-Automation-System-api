using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>
/// Decides what the agent still needs to learn about a lead, and in what order, and records what a turn
/// actually captured.
///
/// Separate from the prompt builder on purpose. WHICH question to ask next is a business decision
/// driven by the tenant's configured priorities; leaving it to the model would make it drift with
/// temperature and with whatever the last person changed in the prompt. The model's job is to phrase
/// the question well, not to choose it.
/// </summary>
public interface IQualificationPlanner
{
    /// <summary>What is known, what is still open, and the next few worth asking - already ordered and
    /// already trimmed. <paramref name="qualificationPaused"/> (a hot lead) returns an empty ask list,
    /// which is how the agent is stopped from qualifying rather than being asked to stop.</summary>
    Task<QualificationPlan> PlanAsync(
        Guid leadId, bool qualificationPaused, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores one turn's extracted values, rejecting any whose key is not in this tenant's schema or
    /// whose value does not fit the field, and superseding an earlier value for the same field when the
    /// customer changed their mind.
    ///
    /// Returns what was actually accepted, which is what the caller records - never what the model
    /// claimed. Does not call SaveChanges: the caller owns the unit of work.
    /// </summary>
    Task<IReadOnlyList<AcceptedField>> CaptureAsync(
        Guid leadId,
        Guid? inboundMessageId,
        IReadOnlyList<AiExtractedField> extracted,
        CancellationToken cancellationToken = default);
}

public record QualificationPlan(
    IReadOnlyList<AiQualificationField> SchemaFields,
    IReadOnlyList<AiCapturedField> Known,
    IReadOnlyList<AiQualificationField> ToAsk,
    bool AllRequiredCaptured,
    int CapturedCount,
    int TotalCount);

/// <summary>A value that passed validation and was stored. <paramref name="Superseded"/> marks the
/// customer having changed a previous answer, which is itself worth surfacing to a salesperson.</summary>
public record AcceptedField(string FieldKey, string RawValue, string? NormalizedValue, bool Superseded);
