using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.MessageTemplates;

internal static class MessageTemplateRuleBuilders
{
    public static IRuleBuilderOptions<T, string> ValidBodyText<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MaximumLength(2000)
            .Must(body => TemplatePlaceholderResolver.TryValidateTokens(body, out _))
            .WithMessage($"Body text may only use these placeholders: {{{{{string.Join("}}, {{", TemplatePlaceholderResolver.KnownTokens)}}}}}.");
}

public class CreateMessageTemplateRequestValidator : AbstractValidator<CreateMessageTemplateRequest>
{
    public CreateMessageTemplateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Language).NotEmpty().MaximumLength(10);
        RuleFor(x => x.WhatsAppTemplateName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BodyText).ValidBodyText();

        RuleFor(x => x.Category)
            .Must(c => Enum.TryParse<TemplateCategory>(c, ignoreCase: true, out _))
            .WithMessage($"Category must be one of: {string.Join(", ", Enum.GetNames<TemplateCategory>())}.");
    }
}

public class UpdateMessageTemplateRequestValidator : AbstractValidator<UpdateMessageTemplateRequest>
{
    public UpdateMessageTemplateRequestValidator()
    {
        RuleFor(x => x.BodyText).ValidBodyText();
    }
}

public class ReviewMessageTemplateRequestValidator : AbstractValidator<ReviewMessageTemplateRequest>
{
    public ReviewMessageTemplateRequestValidator()
    {
        RuleFor(x => x.Status)
            .Must(s => Enum.TryParse<WhatsAppTemplateStatus>(s, ignoreCase: true, out _))
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<WhatsAppTemplateStatus>())}.");
    }
}
