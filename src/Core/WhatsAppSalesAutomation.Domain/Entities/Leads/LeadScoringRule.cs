using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>
/// One configurable contribution to a lead's score. Evaluated in C# after every AI turn, never by the
/// model: a language model does not apply arithmetic consistently, and <c>Lead.ScoreNumeric</c> is CRM
/// state a sales team sorts and filters on - a number that shifts between two runs over the same
/// conversation is not one they can act on. The model's job is to report what happened; deciding what
/// it is worth is the business's.
///
/// Rows are per-tenant, so a clinic-software vendor can weight "requested demo" heavily while a
/// property dealer weights "site visit requested" instead.
/// </summary>
public class LeadScoringRule : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Stable identifier, e.g. "demo_requested". Recorded on every
    /// <see cref="LeadScoreContribution"/> so an old breakdown stays readable after the rule's Points
    /// or DisplayName change.</summary>
    public string RuleKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public LeadScoringRuleType RuleType { get; set; }

    /// <summary>What to match, interpreted per <see cref="RuleType"/> - see that enum's members for
    /// the exact shape each one expects.</summary>
    public string MatchValue { get; set; } = string.Empty;

    /// <summary>Can be negative - "just exploring" should be able to cool a lead down, not only warm
    /// it up.</summary>
    public int Points { get; set; }

    /// <summary>True fires this rule at most once per lead however often it matches; false re-applies
    /// it every turn it matches. Defaults true: a customer asking about price three times is not three
    /// times as hot.</summary>
    public bool OncePerLead { get; set; } = true;

    /// <summary>True means a lead this rule fires on is treated as hot immediately, regardless of the
    /// resulting numeric score - "asked how to pay" should not have to reach 70 points first.</summary>
    public bool MarksLeadHot { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public Guid? LastUpdatedBy { get; set; }
}
