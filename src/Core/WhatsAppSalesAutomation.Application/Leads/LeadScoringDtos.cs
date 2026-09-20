namespace WhatsAppSalesAutomation.Application.Leads;

public record LeadScoringRuleDto(
    Guid Id,
    string RuleKey,
    string DisplayName,
    string RuleType,
    string MatchValue,
    int Points,
    bool OncePerLead,
    bool MarksLeadHot,
    bool IsActive,
    int SortOrder,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>RuleKey is settable only on create - score contributions are recorded against it so an
/// old breakdown stays readable, and changing it would detach that history.</summary>
public record CreateLeadScoringRuleRequest(
    string RuleKey,
    string DisplayName,
    string RuleType,
    string MatchValue,
    int Points,
    bool OncePerLead,
    bool MarksLeadHot,
    int SortOrder);

public record UpdateLeadScoringRuleRequest(
    string DisplayName,
    string RuleType,
    string MatchValue,
    int Points,
    bool OncePerLead,
    bool MarksLeadHot,
    bool IsActive,
    int SortOrder);

/// <summary>One rule or field weight that contributed to a lead's score. This is what makes a
/// configurable score explainable rather than a number the sales team learns to distrust.</summary>
public record LeadScoreContributionDto(
    string SourceKey,
    string DisplayName,
    int Points,
    DateTime AppliedAt);

/// <summary>A lead's score with its workings shown. RawTotal is the sum before clamping, so a tenant
/// whose rules add up to 180 can see that rather than wondering why every hot lead reads exactly 100.</summary>
public record LeadScoreBreakdownDto(
    Guid LeadId,
    int ScoreNumeric,
    int RawTotal,
    string Band,
    bool IsHot,
    string? HotReason,
    DateTime? HotDetectedAt,
    IReadOnlyList<LeadScoreContributionDto> Contributions);

/// <summary>What the rule catalogue offers, for the admin UI's dropdowns - the rule types with what
/// each one's MatchValue means, and the intent names that IntentMatch accepts.</summary>
public record LeadScoringCatalogDto(
    IReadOnlyList<LeadScoringRuleTypeDto> RuleTypes,
    IReadOnlyList<string> Intents,
    IReadOnlyList<string> FieldKeys);

public record LeadScoringRuleTypeDto(string Name, string MatchValueMeaning, string Example);
