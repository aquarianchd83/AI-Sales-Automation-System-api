using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Tags;

internal static class TagRuleBuilders
{
    /// <summary>100 matches the column length; commas and semicolons are the import file's tag
    /// separators, so a name containing one could never be round-tripped through an import.</summary>
    public static IRuleBuilderOptions<T, string> ValidTagName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MaximumLength(100)
            .Must(name => !name.Contains(',') && !name.Contains(';'))
            .WithMessage("Tag name cannot contain ',' or ';'.");
}

public class CreateTagRequestValidator : AbstractValidator<CreateTagRequest>
{
    public CreateTagRequestValidator()
    {
        RuleFor(x => x.Name).ValidTagName();
    }
}

public class UpdateTagRequestValidator : AbstractValidator<UpdateTagRequest>
{
    public UpdateTagRequestValidator()
    {
        RuleFor(x => x.Name).ValidTagName();
    }
}
