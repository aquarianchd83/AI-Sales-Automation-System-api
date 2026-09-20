using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Ai;

/// <summary>
/// One output check that did not pass on one AI turn.
///
/// Recorded as rows rather than as a list on the turn itself because the useful question is aggregate:
/// which check fires most, is it rising after a prompt change, and is it blocking replies or only
/// dropping parts of them. A comma-separated column would answer none of those without reading every
/// row back out and splitting it.
///
/// <para>A row here is not a bug report about the model in isolation. A spike in UngroundedNumber
/// usually means the knowledge base is missing the price list, not that the model got worse.</para>
/// </summary>
public class AiInteractionValidationFailure : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid AiInteractionId { get; set; }

    /// <summary>The check's stable code - "UngroundedNumber", "InternalTermLeak", and so on. Stable
    /// because it is the grouping key of every report built on this table; the human-facing wording
    /// lives in the UI, where it can be reworded without breaking a month of history.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Whether this check stopped the reply being sent. Snapshotted per row rather than
    /// looked up from the validator, so a check that changes from advisory to blocking does not
    /// silently rewrite what happened before the change.</summary>
    public bool Blocking { get; set; }

    /// <summary>What specifically failed, e.g. which term leaked or which number was unsupported.
    /// Truncated: this is for reading a handful of examples, not for matching on.</summary>
    public string? Detail { get; set; }
}
