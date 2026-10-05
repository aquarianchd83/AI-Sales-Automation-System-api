using System.Text.RegularExpressions;
using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Setup;

public class SaveSetupVersionRequestValidator : AbstractValidator<SaveSetupVersionRequest>
{
    public SaveSetupVersionRequestValidator()
    {
        RuleFor(x => x.ReleaseNotes).MaximumLength(1000);
        RuleFor(x => x.ValidityDays).InclusiveBetween(1, 3650).When(x => x.ValidityDays.HasValue);
    }
}

public class CreateSetupVersionRequestValidator : AbstractValidator<CreateSetupVersionRequest>
{
    public CreateSetupVersionRequestValidator()
    {
        RuleFor(x => x.ReleaseNotes).MaximumLength(1000);
    }
}

public class SaveRequirementRequestValidator : AbstractValidator<SaveRequirementRequest>
{
    public const int MaxOptions = 50;

    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_]{1,59}$", RegexOptions.Compiled);
    private static readonly Regex SectionPattern = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled);

    public SaveRequirementRequestValidator()
    {
        RuleFor(x => x.FieldKey).NotEmpty().Matches(KeyPattern)
            .WithMessage("Field key must be 2-60 characters: lowercase letters, digits and underscores, starting with a letter.");
        RuleFor(x => x.Label).NotEmpty().MaximumLength(150);
        RuleFor(x => x.HelpText).MaximumLength(500);
        RuleFor(x => x.FieldType).IsInEnum();
        RuleFor(x => x.DefaultValue).MaximumLength(1000);
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 100_000);
        RuleFor(x => x.Section).NotEmpty().Matches(SectionPattern)
            .WithMessage("Section must be lowercase letters, digits and underscores, starting with a letter.");

        RuleFor(x => x.MetricKey)
            .Must(m => m is null || SetupMetrics.All.Contains(m))
            .WithMessage($"Metric must be one of: {string.Join(", ", SetupMetrics.All)}.");

        RuleFor(x => x.Options)
            .Must(o => o is { Count: > 0 })
            .WithMessage("Add at least one option for a choice field.")
            .When(x => IsChoice(x.FieldType));
        RuleFor(x => x.Options)
            .Must(o => o is null || o.Count <= MaxOptions)
            .WithMessage($"A field can have at most {MaxOptions} options.");
        RuleFor(x => x.Options)
            .Must(o => o is null || (o.All(i => !string.IsNullOrWhiteSpace(i.Value) && !string.IsNullOrWhiteSpace(i.Label) && i.Value.Length <= 100 && i.Label.Length <= 150)
                                     && o.Select(i => i.Value.Trim().ToLowerInvariant()).Distinct().Count() == o.Count))
            .WithMessage("Every option needs a unique value and a label (values up to 100, labels up to 150 characters).");

        RuleFor(x => x.Validation!.Pattern).MaximumLength(300).When(x => x.Validation is not null);
        RuleFor(x => x.Validation!.PatternMessage).MaximumLength(200).When(x => x.Validation is not null);
        RuleFor(x => x.Validation)
            .Must(v => v is null || v.Min is null || v.Max is null || v.Min <= v.Max)
            .WithMessage("Minimum cannot be greater than maximum.");
        RuleFor(x => x.Validation)
            .Must(v => v is null || v.MinLength is null || v.MaxLength is null || v.MinLength <= v.MaxLength)
            .WithMessage("Minimum length cannot be greater than maximum length.");

        RuleFor(x => x.Condition!.FieldKey).NotEmpty().Matches(KeyPattern).When(x => x.Condition is not null);
        RuleFor(x => x.Condition!.Operator).IsInEnum().When(x => x.Condition is not null);
        RuleFor(x => x.Condition)
            .Must(c => c is null || c.Operator is SetupConditionOperator.Empty or SetupConditionOperator.NotEmpty || !string.IsNullOrWhiteSpace(c.Value))
            .WithMessage("Enter the answer the condition compares against.");
        RuleFor(x => x.Condition)
            .Must((request, c) => c is null || !string.Equals(c.FieldKey, request.FieldKey, StringComparison.OrdinalIgnoreCase))
            .WithMessage("A field cannot depend on itself.");
    }

    public static bool IsChoice(SetupFieldType type) => type is SetupFieldType.Dropdown or SetupFieldType.MultiSelect or SetupFieldType.Radio;
}
