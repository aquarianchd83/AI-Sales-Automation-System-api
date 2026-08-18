using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Conversations;

public class ChangeConversationModeRequestValidator : AbstractValidator<ChangeConversationModeRequest>
{
    public ChangeConversationModeRequestValidator()
    {
        RuleFor(x => x.Mode)
            .Must(m => Enum.TryParse<ConversationMode>(m, ignoreCase: true, out _))
            .WithMessage($"Mode must be one of: {string.Join(", ", Enum.GetNames<ConversationMode>())}.");
    }
}

public class SendConversationMessageRequestValidator : AbstractValidator<SendConversationMessageRequest>
{
    public SendConversationMessageRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.Text) ^ x.MessageTemplateId.HasValue)
            .WithMessage("Provide exactly one of Text or MessageTemplateId, not both and not neither.");

        RuleFor(x => x.Text).MaximumLength(4000).When(x => !string.IsNullOrWhiteSpace(x.Text));
    }
}
