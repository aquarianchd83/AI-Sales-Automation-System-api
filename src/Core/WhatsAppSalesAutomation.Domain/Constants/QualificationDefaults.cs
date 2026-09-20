using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Constants;

/// <summary>
/// The qualification schema and scoring rules every tenant starts with.
///
/// These are not arbitrary starter content: they reproduce, exactly, the behaviour the platform had
/// before qualification was configurable. The three fields are the three that used to be hardcoded on
/// <c>AiExtractedEntities</c>, their <see cref="QualificationFieldSeed.ScoreWeight"/> values are the
/// 30/30/20 the old <c>LeadService.ComputeScoreNumeric</c> applied, and the two seeded rules are the
/// +/-20 it applied for Negotiation/Complaint. A tenant that never touches this configuration must
/// score every lead identically to how it scored before - anything else is a silent regression in a
/// number the sales team already trusts.
///
/// Note there is deliberately no "budget provided" RULE alongside the budget field's ScoreWeight.
/// Seeding both would add its points on top of the weight and change existing scores, which is the one
/// thing this seed exists to prevent. A tenant that wants a rule like that adds it themselves.
/// </summary>
public static class QualificationDefaults
{
    public const string BudgetKey = "budget";
    public const string InterestKey = "interest";
    public const string PurchaseTimelineKey = "purchase_timeline";

    /// <summary>The field keys whose values are mirrored onto <c>Lead.Budget</c>/<c>Interest</c>/
    /// <c>PurchaseTimeline</c> when captured, so the existing lead list, filters and reports keep
    /// working while the UI moves to dynamic fields.</summary>
    public static readonly IReadOnlySet<string> MirroredKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BudgetKey, InterestKey, PurchaseTimelineKey };

    public static readonly IReadOnlyList<QualificationFieldSeed> Fields = new[]
    {
        new QualificationFieldSeed(
            BudgetKey, "Budget",
            "How much the customer is willing to spend. Accept ranges and approximations as the customer states them.",
            "Do you have an approximate budget in mind?",
            QualificationDataType.Currency, IsRequired: true, Priority: 80, ScoreWeight: 30, SortOrder: 1),

        new QualificationFieldSeed(
            InterestKey, "Interest",
            "What the customer is actually looking for - the product, service or category.",
            "What exactly are you looking for?",
            QualificationDataType.Text, IsRequired: true, Priority: 70, ScoreWeight: 30, SortOrder: 2),

        new QualificationFieldSeed(
            PurchaseTimelineKey, "Purchase timeline",
            "When the customer intends to go ahead. Keep their own phrasing - do not convert vague timing into a date.",
            "When are you planning to go ahead?",
            QualificationDataType.Text, IsRequired: false, Priority: 60, ScoreWeight: 20, SortOrder: 3)
    };

    public static readonly IReadOnlyList<LeadScoringRuleSeed> ScoringRules = new[]
    {
        new LeadScoringRuleSeed(
            "negotiation_intent", "Negotiating terms",
            LeadScoringRuleType.IntentMatch, nameof(CustomerIntent.Negotiation),
            Points: 20, OncePerLead: true, MarksLeadHot: false, SortOrder: 1),

        new LeadScoringRuleSeed(
            "complaint_intent", "Complaint raised",
            LeadScoringRuleType.IntentMatch, nameof(CustomerIntent.Complaint),
            Points: -20, OncePerLead: true, MarksLeadHot: false, SortOrder: 2)
    };
}

public record QualificationFieldSeed(
    string FieldKey,
    string DisplayName,
    string Description,
    string Question,
    QualificationDataType DataType,
    bool IsRequired,
    int Priority,
    int ScoreWeight,
    int SortOrder);

public record LeadScoringRuleSeed(
    string RuleKey,
    string DisplayName,
    LeadScoringRuleType RuleType,
    string MatchValue,
    int Points,
    bool OncePerLead,
    bool MarksLeadHot,
    int SortOrder);
