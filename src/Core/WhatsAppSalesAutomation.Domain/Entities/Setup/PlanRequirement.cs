using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Setup;

/// <summary>
/// One question a plan version asks. Global (belongs to a <see cref="PlanSetupVersion"/>, which belongs to the
/// platform), and editable only while that version is a Draft.
///
/// <see cref="FieldKey"/> is the stable identity of an answer ACROSS versions and plans: an application's stored
/// answers are keyed by it, which is what lets "brand_name" survive a Basic -> Lead Generation plan change
/// untouched while "lead_source" is asked fresh.
///
/// The structured bits (options, validation rules) are JSON columns because they are only ever read and written
/// whole, together with their field; the condition is three plain columns because "who depends on whom" is the
/// one thing an admin screen and the evaluator both need to look at without parsing anything.
/// </summary>
public class PlanRequirement : BaseEntity
{
    public Guid PlanSetupVersionId { get; set; }

    public string FieldKey { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? HelpText { get; set; }

    public SetupFieldType FieldType { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>Used as the answer when the tenant has not given one. Same string form the answer is stored in.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>JSON array of {"value","label"} for Dropdown / MultiSelect / Radio.</summary>
    public string? OptionsJson { get; set; }

    /// <summary>JSON object: min, max (numbers), minLength, maxLength, pattern, patternMessage.</summary>
    public string? ValidationJson { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>Wizard step key ("business", "audience", ...). A section no active field uses is never shown.</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>The field this one depends on. Null = always shown. See <see cref="ConditionOperator"/>.</summary>
    public string? ConditionFieldKey { get; set; }

    public SetupConditionOperator? ConditionOperator { get; set; }

    public string? ConditionValue { get; set; }

    /// <summary>Tags the answer as an input to the revenue/cost projection (see SetupMetrics) so a plan's
    /// configuration can be turned into expected revenue, cost and ROI without a separate questionnaire.</summary>
    public string? MetricKey { get; set; }

    public bool IsActive { get; set; } = true;
}
