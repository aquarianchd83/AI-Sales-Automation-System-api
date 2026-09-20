using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public static class LeadScoringLimits
{
    public const int RuleKey = 60;
    public const int DisplayName = 100;
    public const int MatchValue = 200;

    /// <summary>Points are bounded well inside the 0-100 clamp so one mistyped rule cannot make every
    /// lead hot on its own. A tenant who wants a single decisive signal sets MarksLeadHot instead,
    /// which is the honest way to express "this alone means they are ready".</summary>
    public const int MinPoints = -50;
    public const int MaxPoints = 50;

    public const int MaxRulesPerTenant = 40;

    public static string RuleTypeMessage =>
        $"Rule type must be one of: {string.Join(", ", Enum.GetNames<LeadScoringRuleType>())}.";

    /// <summary>Checks MatchValue against what the chosen rule type actually expects. Without this a
    /// rule saves happily and then never fires, which is the hardest kind of configuration bug to
    /// notice - nothing errors, the score is just quietly wrong.</summary>
    public static bool MatchValueFitsRuleType(string ruleType, string? matchValue)
    {
        if (!Enum.TryParse<LeadScoringRuleType>(ruleType, ignoreCase: true, out var parsed))
            return true; // the RuleType rule already reports this

        var value = matchValue?.Trim() ?? string.Empty;

        return parsed switch
        {
            LeadScoringRuleType.IntentMatch => Enum.TryParse<CustomerIntent>(value, ignoreCase: true, out _),
            LeadScoringRuleType.FieldValueMatch => value.Contains('=') && value.Split('=', 2)[0].Trim().Length > 0,
            LeadScoringRuleType.TimelineWithinDays => int.TryParse(value, out var days) && days is > 0 and <= 3650,
            LeadScoringRuleType.BuyingIntentDetected => true, // MatchValue is ignored
            _ => value.Length > 0
        };
    }

    public static string MatchValueMessage(string ruleType) =>
        Enum.TryParse<LeadScoringRuleType>(ruleType, ignoreCase: true, out var parsed)
            ? parsed switch
            {
                LeadScoringRuleType.IntentMatch =>
                    $"For IntentMatch, match value must be one of: {string.Join(", ", Enum.GetNames<CustomerIntent>())}.",
                LeadScoringRuleType.FieldValueMatch =>
                    "For FieldValueMatch, match value must look like 'field_key=value'.",
                LeadScoringRuleType.TimelineWithinDays =>
                    "For TimelineWithinDays, match value must be a number of days between 1 and 3650.",
                _ => "Match value is required for this rule type."
            }
            : "Match value is required for this rule type.";
}

public class CreateLeadScoringRuleRequestValidator : AbstractValidator<CreateLeadScoringRuleRequest>
{
    public CreateLeadScoringRuleRequestValidator()
    {
        RuleFor(x => x.RuleKey)
            .NotEmpty()
            .MaximumLength(LeadScoringLimits.RuleKey)
            .Must(k => string.IsNullOrEmpty(k) || QualificationFieldLimits.KeyPattern.IsMatch(k))
            .WithMessage("Rule key must be lowercase snake_case starting with a letter, e.g. 'demo_requested'.");

        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(LeadScoringLimits.DisplayName);

        RuleFor(x => x.RuleType)
            .Must(t => Enum.TryParse<LeadScoringRuleType>(t, ignoreCase: true, out _))
            .WithMessage(LeadScoringLimits.RuleTypeMessage);

        RuleFor(x => x.MatchValue).MaximumLength(LeadScoringLimits.MatchValue);

        RuleFor(x => x.MatchValue)
            .Must((request, value) => LeadScoringLimits.MatchValueFitsRuleType(request.RuleType, value))
            .WithMessage(request => LeadScoringLimits.MatchValueMessage(request.RuleType));

        RuleFor(x => x.Points)
            .InclusiveBetween(LeadScoringLimits.MinPoints, LeadScoringLimits.MaxPoints)
            .Must(p => p != 0)
            .WithMessage("A rule worth zero points has no effect - remove it or give it a value.");
    }
}

public class UpdateLeadScoringRuleRequestValidator : AbstractValidator<UpdateLeadScoringRuleRequest>
{
    public UpdateLeadScoringRuleRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(LeadScoringLimits.DisplayName);

        RuleFor(x => x.RuleType)
            .Must(t => Enum.TryParse<LeadScoringRuleType>(t, ignoreCase: true, out _))
            .WithMessage(LeadScoringLimits.RuleTypeMessage);

        RuleFor(x => x.MatchValue).MaximumLength(LeadScoringLimits.MatchValue);

        RuleFor(x => x.MatchValue)
            .Must((request, value) => LeadScoringLimits.MatchValueFitsRuleType(request.RuleType, value))
            .WithMessage(request => LeadScoringLimits.MatchValueMessage(request.RuleType));

        RuleFor(x => x.Points)
            .InclusiveBetween(LeadScoringLimits.MinPoints, LeadScoringLimits.MaxPoints)
            .Must(p => p != 0)
            .WithMessage("A rule worth zero points has no effect - remove it or give it a value.");
    }
}
