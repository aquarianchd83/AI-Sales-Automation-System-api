using System.Text.RegularExpressions;
using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public static class QualificationFieldLimits
{
    public const int FieldKey = 60;
    public const int DisplayName = 100;
    public const int Description = 500;
    public const int Question = 300;
    public const int ValidationPattern = 500;
    public const int AllowedValue = 100;
    public const int MaxAllowedValues = 50;

    /// <summary>Caps how many fields one tenant can configure. Not a storage limit - the prompt only
    /// ever shows the top few - but a schema with fifty fields is a questionnaire someone will blame
    /// the AI for, and the ceiling is the honest place to say so.</summary>
    public const int MaxFieldsPerTenant = 25;

    /// <summary>snake_case starting with a letter. This value becomes an enum entry in the model's
    /// tool schema, so anything a JSON property name cannot safely be is rejected here.</summary>
    public static readonly Regex KeyPattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Compiles the pattern so a malformed one is rejected at configuration time rather than
    /// on an inbound customer message, where it would surface as a failed capture nobody can explain.</summary>
    public static bool IsValidRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A choice field with no options cannot validate anything, so whatever the model
    /// returned would be stored unchecked - exactly the "unconstrained value in a constrained field"
    /// the AllowedValues check exists to prevent.</summary>
    public static bool ChoiceFieldHasOptions(string dataType, IReadOnlyList<string>? allowedValues)
    {
        if (!Enum.TryParse<QualificationDataType>(dataType, ignoreCase: true, out var parsed))
            return true; // the DataType rule already reports this

        var needsOptions = parsed is QualificationDataType.SingleChoice or QualificationDataType.MultiChoice;
        return !needsOptions || allowedValues is { Count: > 0 };
    }

    public static string DataTypeMessage =>
        $"Data type must be one of: {string.Join(", ", Enum.GetNames<QualificationDataType>())}.";
}

public class CreateQualificationFieldRequestValidator : AbstractValidator<CreateQualificationFieldRequest>
{
    public CreateQualificationFieldRequestValidator()
    {
        RuleFor(x => x.FieldKey)
            .NotEmpty()
            .MaximumLength(QualificationFieldLimits.FieldKey)
            .Must(k => string.IsNullOrEmpty(k) || QualificationFieldLimits.KeyPattern.IsMatch(k))
            .WithMessage("Field key must be lowercase snake_case starting with a letter, e.g. 'property_type'.");

        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(QualificationFieldLimits.DisplayName);
        RuleFor(x => x.Description).MaximumLength(QualificationFieldLimits.Description);
        RuleFor(x => x.Question).NotEmpty().MaximumLength(QualificationFieldLimits.Question);

        RuleFor(x => x.DataType)
            .Must(d => Enum.TryParse<QualificationDataType>(d, ignoreCase: true, out _))
            .WithMessage(QualificationFieldLimits.DataTypeMessage);

        RuleFor(x => x.Priority).InclusiveBetween(0, 100);
        RuleFor(x => x.ScoreWeight).InclusiveBetween(-100, 100);

        RuleFor(x => x.AllowedValues)
            .Must(v => v is null || v.Count <= QualificationFieldLimits.MaxAllowedValues)
            .WithMessage($"At most {QualificationFieldLimits.MaxAllowedValues} allowed values.");

        RuleForEach(x => x.AllowedValues).NotEmpty().MaximumLength(QualificationFieldLimits.AllowedValue);

        RuleFor(x => x.AllowedValues)
            .Must((request, values) => QualificationFieldLimits.ChoiceFieldHasOptions(request.DataType, values))
            .WithMessage("SingleChoice and MultiChoice fields need at least one allowed value.");

        RuleFor(x => x.ValidationPattern)
            .MaximumLength(QualificationFieldLimits.ValidationPattern)
            .Must(QualificationFieldLimits.IsValidRegex)
            .WithMessage("Validation pattern is not a valid regular expression.");
    }
}

public class UpdateQualificationFieldRequestValidator : AbstractValidator<UpdateQualificationFieldRequest>
{
    public UpdateQualificationFieldRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(QualificationFieldLimits.DisplayName);
        RuleFor(x => x.Description).MaximumLength(QualificationFieldLimits.Description);
        RuleFor(x => x.Question).NotEmpty().MaximumLength(QualificationFieldLimits.Question);

        RuleFor(x => x.DataType)
            .Must(d => Enum.TryParse<QualificationDataType>(d, ignoreCase: true, out _))
            .WithMessage(QualificationFieldLimits.DataTypeMessage);

        RuleFor(x => x.Priority).InclusiveBetween(0, 100);
        RuleFor(x => x.ScoreWeight).InclusiveBetween(-100, 100);

        RuleFor(x => x.AllowedValues)
            .Must(v => v is null || v.Count <= QualificationFieldLimits.MaxAllowedValues)
            .WithMessage($"At most {QualificationFieldLimits.MaxAllowedValues} allowed values.");

        RuleForEach(x => x.AllowedValues).NotEmpty().MaximumLength(QualificationFieldLimits.AllowedValue);

        RuleFor(x => x.AllowedValues)
            .Must((request, values) => QualificationFieldLimits.ChoiceFieldHasOptions(request.DataType, values))
            .WithMessage("SingleChoice and MultiChoice fields need at least one allowed value.");

        RuleFor(x => x.ValidationPattern)
            .MaximumLength(QualificationFieldLimits.ValidationPattern)
            .Must(QualificationFieldLimits.IsValidRegex)
            .WithMessage("Validation pattern is not a valid regular expression.");
    }
}

public class ReorderQualificationFieldsRequestValidator : AbstractValidator<ReorderQualificationFieldsRequest>
{
    public ReorderQualificationFieldsRequestValidator()
    {
        RuleFor(x => x.OrderedIds)
            .NotEmpty().WithMessage("At least one field id is required.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("The same field id appears more than once.");
    }
}

public class SetLeadQualificationValueRequestValidator : AbstractValidator<SetLeadQualificationValueRequest>
{
    public SetLeadQualificationValueRequestValidator()
    {
        RuleFor(x => x.Value).NotEmpty().MaximumLength(1000);
    }
}
