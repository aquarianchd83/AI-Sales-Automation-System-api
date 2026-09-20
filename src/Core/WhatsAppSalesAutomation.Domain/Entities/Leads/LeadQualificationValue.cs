using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>
/// One captured answer for one <see cref="QualificationField"/> on one <see cref="Lead"/>.
///
/// Append-only with a supersede flag rather than update-in-place: the agent must both know the current
/// budget ("Actually my budget is 60,000" replaces 50,000) and know it already asked. Keeping both
/// rows answers both, while an in-place update would lose the fact that the customer changed their
/// mind - which is itself a sales signal worth showing the human who takes over.
/// </summary>
public class LeadQualificationValue : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid LeadId { get; set; }

    public Guid FieldId { get; set; }

    /// <summary>Denormalized from <see cref="QualificationField.FieldKey"/> so a lead's captured
    /// answers stay readable after the field is deactivated, renamed or soft-deleted.</summary>
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>Exactly what the customer said, e.g. "around 1 cr". Never normalized away - an agent
    /// reading the lead wants the customer's own words, not our interpretation of them.</summary>
    public string RawValue { get; set; } = string.Empty;

    /// <summary>Machine-comparable form: a decimal string for Number/Currency, an ISO date for Date,
    /// the matched option for SingleChoice. Null when the raw value could not be normalized, which is
    /// not an error - it only means filters and value-matching scoring rules skip this one.</summary>
    public string? NormalizedValue { get; set; }

    /// <summary>The inbound Message this was extracted from, so the lead screen can show where the
    /// customer said it rather than an unattributed value. Null for a value a human typed in.</summary>
    public Guid? CapturedFromMessageId { get; set; }

    /// <summary>Null = extracted by the AI. Set = a human agent entered or corrected it on the lead
    /// screen - same "null means system" convention as <see cref="LeadActivity.CreatedBy"/>.</summary>
    public Guid? CapturedByUserId { get; set; }

    /// <summary>0.0-1.0, the model's own confidence in this specific extraction. Below
    /// <c>AiOptions.MinFieldExtractionConfidence</c> the value is stored but does NOT count as
    /// captured when choosing the next question, so a shaky extraction never stops the agent from
    /// asking properly. A human-entered value is always 1.0.</summary>
    public double ExtractionConfidence { get; set; }

    /// <summary>True once a later value for the same (LeadId, FieldKey) replaced this one. At most one
    /// row per pair has this false, enforced by a filtered unique index.</summary>
    public bool IsSuperseded { get; set; }
}
