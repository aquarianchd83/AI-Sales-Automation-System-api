using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public class CreateCampaignRequestValidator : AbstractValidator<CreateCampaignRequest>
{
    public CreateCampaignRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.ScheduledStartAt)
            .GreaterThan(_ => DateTime.UtcNow)
            .When(x => x.ScheduledStartAt.HasValue)
            .WithMessage("Scheduled start must be in the future.");
    }
}

public class UpdateCampaignRequestValidator : AbstractValidator<UpdateCampaignRequest>
{
    public UpdateCampaignRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public class UpsertCampaignStepRequestValidator : AbstractValidator<UpsertCampaignStepRequest>
{
    public UpsertCampaignStepRequestValidator()
    {
        RuleFor(x => x.StepType)
            .Must(s => Enum.TryParse<CampaignStepType>(s, ignoreCase: true, out _))
            .WithMessage($"StepType must be one of: {string.Join(", ", Enum.GetNames<CampaignStepType>())}.");

        RuleFor(x => x.MessageText)
            .NotEmpty()
            .MaximumLength(2000)
            .Must(body => TemplatePlaceholderResolver.TryValidateTokens(body, out _))
            .WithMessage($"Message text may only use these placeholders: {{{{{string.Join("}}, {{", TemplatePlaceholderResolver.KnownTokens)}}}}}.");

        RuleFor(x => x.DelayDaysAfterPrevious)
            .GreaterThanOrEqualTo(0)
            .When(x => Enum.TryParse<CampaignStepType>(x.StepType, true, out var t) && t != CampaignStepType.Initial)
            .WithMessage("Follow-up steps need a non-negative delay.");

        RuleFor(x => x.DelayDaysAfterPrevious)
            .Equal(0)
            .When(x => Enum.TryParse<CampaignStepType>(x.StepType, true, out var t) && t == CampaignStepType.Initial)
            .WithMessage("The Initial step has no delay - it is sent when the campaign starts.");
    }
}

public class SetCampaignAudienceRequestValidator : AbstractValidator<SetCampaignAudienceRequest>
{
    public SetCampaignAudienceRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => (x.TagNames?.Count ?? 0) + (x.CustomerIds?.Count ?? 0) > 0)
            .WithMessage("Provide at least one tag name or customer id.");
    }
}
