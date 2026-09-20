using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>
/// One rule or field weight that contributed to a lead's current score, and for how many points.
///
/// Exists so "why is this lead 87?" has an answer on the lead screen. A configurable scoring system
/// whose breakdown is invisible is a black box, and sales staff learn to distrust a number they cannot
/// take apart - at which point the configurability has bought nothing.
///
/// Also load-bearing, not only explanatory: it is how <c>LeadScoringRule.OncePerLead</c> is enforced,
/// since "has this rule already fired for this lead" is exactly a lookup over these rows.
/// </summary>
public class LeadScoreContribution : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid LeadId { get; set; }

    /// <summary>The rule that fired, or null when this contribution came from a
    /// <see cref="QualificationField.ScoreWeight"/> rather than a rule.</summary>
    public Guid? RuleId { get; set; }

    /// <summary>Namespaced source identifier: "rule:{RuleKey}" for a scoring rule, "field:{FieldKey}"
    /// for a field weight. The prefix is what keeps the two apart - a tenant is free to name a scoring
    /// rule "budget" while also having a "budget" qualification field, and without it those two would
    /// collide on the once-per-lead unique index and silently suppress one another.
    ///
    /// Denormalized rather than resolved through RuleId so a breakdown stays readable after the rule
    /// or field it came from is gone.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Human-facing label as it was when this fired.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public int Points { get; set; }

    /// <summary>True when this came from a rule whose OncePerLead was set - which is what makes the
    /// "already fired?" lookup a filtered one rather than a scan of every contribution ever recorded
    /// for the lead.</summary>
    public bool IsOnce { get; set; }

    /// <summary>The AI turn that triggered it, when there was one. Null for a recompute triggered by a
    /// human edit or a configuration change.</summary>
    public Guid? TriggeredByInteractionId { get; set; }

    public DateTime AppliedAt { get; set; }
}
