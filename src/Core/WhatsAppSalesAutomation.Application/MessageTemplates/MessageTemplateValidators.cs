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

    /// <summary>Meta's own real constraint, confirmed against a live rejection (error_subcode
    /// 2388046, "The message template name can only have lower-case letters and underscores") -
    /// Meta's public docs also allow digits, so this is lower-case letters/digits/underscores rather
    /// than the stricter letters-and-underscores-only wording of that one error message. Enforced here
    /// so a bad name is caught at save time, not discovered as a push failure hours later.</summary>
    public static IRuleBuilderOptions<T, string> ValidWhatsAppTemplateName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MaximumLength(200)
            .Matches("^[a-z0-9_]+$")
            .WithMessage("WhatsApp template name may only contain lower-case letters, digits, and underscores.");
}

public class CreateMessageTemplateRequestValidator : AbstractValidator<CreateMessageTemplateRequest>
{
    public CreateMessageTemplateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Language).NotEmpty().MaximumLength(10);
        RuleFor(x => x.WhatsAppTemplateName).ValidWhatsAppTemplateName();
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

        // Only validated when provided - see MessageTemplateService.UpdateAsync for why a rename is
        // rejected outright (MetaTemplateId already set) before this format check would even matter.
        RuleFor(x => x.WhatsAppTemplateName!).ValidWhatsAppTemplateName().When(x => x.WhatsAppTemplateName is not null);
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
