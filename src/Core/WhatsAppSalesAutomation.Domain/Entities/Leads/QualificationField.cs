using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>
/// One thing this tenant wants its AI sales agent to find out about a customer. The set of these rows
/// IS the tenant's qualification schema: a property dealer configures budget/location/property_type, a
/// clinic-software vendor configures doctor_count/current_system/patient_volume, and neither one's
/// fields appear in the other's prompt.
///
/// Replaces the three hardcoded fields on <c>AiExtractedEntities</c> (Budget/Interest/
/// PurchaseTimeline), which every tenant got whether or not they meant anything for that business.
/// Those three survive as seeded defaults for existing tenants, so nothing changes for them on the day
/// this ships - see the Phase 7 design doc's backward-compatibility section.
/// </summary>
public class QualificationField : BaseEntity, ITenantOwned, ISoftDelete
{
    public Guid TenantId { get; set; }

    /// <summary>snake_case, unique per tenant, stable. This is what the model sees as a value in the
    /// tool schema's field_key enum and what <see cref="LeadQualificationValue"/> stores, so renaming
    /// <see cref="DisplayName"/> never orphans already-captured values.</summary>
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>What a human sees in the admin UI and on the lead detail screen, e.g. "Budget".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What this field means, written for the model. Goes into the tool schema as the
    /// field's description - this is what teaches it to recognise "around 1 cr" as a budget without
    /// being shown that exact phrasing.</summary>
    public string? Description { get; set; }

    /// <summary>The natural-language question to ask when this field is the next one to collect, e.g.
    /// "Do you have an approximate budget in mind?". A suggestion rather than a script: the prompt
    /// explicitly allows rephrasing it to fit the conversation, because a question pasted verbatim
    /// into a reply is what makes an agent sound like a form.</summary>
    public string Question { get; set; } = string.Empty;

    public QualificationDataType DataType { get; set; } = QualificationDataType.Text;

    /// <summary>Required fields are asked before optional ones at equal <see cref="Priority"/>, and a
    /// lead with any required field still missing is never treated as fully qualified.</summary>
    public bool IsRequired { get; set; }

    /// <summary>0-100, decides which missing field is asked next. Not the same as
    /// <see cref="ScoreWeight"/>: a field can be urgent to ask while contributing little to the score
    /// (a delivery pin code), or contribute heavily while being worth waiting for (budget).</summary>
    public int Priority { get; set; } = 50;

    /// <summary>Points added to the lead's score once this field has an accepted value. Lives here
    /// rather than only in <see cref="LeadScoringRule"/> because "we learned the budget" is the most
    /// common scoring event, and forcing a separate rule row for every field would be noise.</summary>
    public int ScoreWeight { get; set; }

    /// <summary>JSON array of allowed values for SingleChoice/MultiChoice, e.g.
    /// <c>["1BHK","2BHK","3BHK"]</c>. Null for free-form types. Enforced when a value is captured: a
    /// value outside the list is rejected rather than stored.</summary>
    public string? AllowedValuesJson { get; set; }

    /// <summary>Optional regex a captured value must match, in .NET syntax. Same rejection behaviour
    /// as <see cref="AllowedValuesJson"/>. Evaluated with a timeout - a pattern is tenant input, and
    /// a catastrophically backtracking one must not be able to stall an inbound message.</summary>
    public string? ValidationPattern { get; set; }

    /// <summary>False takes the field out of the schema without deleting values already captured for
    /// it - the tenant's equivalent of deprecating a field. Distinct from <see cref="IsDeleted"/>,
    /// which hides it from the admin UI as well.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Manual ordering within the admin UI, and the last tie-break when two fields have the
    /// same IsRequired and Priority.</summary>
    public int SortOrder { get; set; }

    public Guid? LastUpdatedBy { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }
}
