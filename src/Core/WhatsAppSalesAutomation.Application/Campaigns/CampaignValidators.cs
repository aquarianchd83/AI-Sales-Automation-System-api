using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public class CreateCampaignRequestValidator : AbstractValidator<CreateCampaignRequest>
{
    public CreateCampaignRequestValidator(IDateTimeProvider dateTime)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);

        // Compared against IstNow, not UtcNow: ScheduledStartAt is pinned to IST (see
        // Campaign.ScheduledStartAt) - comparing IST digits against a UTC clock would be wrong by
        // up to 5:30 near midnight, which is exactly the bug this pinning exists to prevent.
        RuleFor(x => x.ScheduledStartAt)
            .GreaterThan(_ => dateTime.IstNow)
            .When(x => x.ScheduledStartAt.HasValue)
            .WithMessage("Scheduled start must be in the future (India Standard Time).");
    }
}

public class UpdateCampaignRequestValidator : AbstractValidator<UpdateCampaignRequest>
{
    public UpdateCampaignRequestValidator(IDateTimeProvider dateTime)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);

        // A null ScheduledStartAt is fine (see CampaignService.UpdateAsync - it falls the campaign
        // back to Draft), but a non-null one must still be in the future IST, same as on create.
        RuleFor(x => x.ScheduledStartAt)
            .GreaterThan(_ => dateTime.IstNow)
            .When(x => x.ScheduledStartAt.HasValue)
            .WithMessage("Scheduled start must be in the future (India Standard Time).");
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
