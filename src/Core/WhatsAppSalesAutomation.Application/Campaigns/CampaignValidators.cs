using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Campaigns;

public class CreateCampaignRequestValidator : AbstractValidator<CreateCampaignRequest>
{
    public CreateCampaignRequestValidator(ITenantTimeZoneProvider tenantTimeZone)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);

        // Compared against the tenant's own local "now" (ITenantTimeZoneProvider), not UtcNow:
        // ScheduledStartAt is pinned to the tenant's own timezone (see Campaign.ScheduledStartAt) -
        // comparing those digits against a UTC clock would be wrong by up to the tenant's own UTC
        // offset near midnight, which is exactly the bug this pinning exists to prevent. MustAsync
        // rather than GreaterThan since resolving "now" requires an ambient-tenant DB read.
        RuleFor(x => x.ScheduledStartAt)
            .MustAsync(async (value, cancellationToken) => value!.Value > await tenantTimeZone.GetLocalNowAsync(cancellationToken))
            .When(x => x.ScheduledStartAt.HasValue)
            .WithMessage("Scheduled start must be in the future.");
    }
}

public class UpdateCampaignRequestValidator : AbstractValidator<UpdateCampaignRequest>
{
    public UpdateCampaignRequestValidator(ITenantTimeZoneProvider tenantTimeZone)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);

        // A null ScheduledStartAt is fine (see CampaignService.UpdateAsync - it falls the campaign
        // back to Draft), but a non-null one must still be in the tenant's own future, same as on
        // create - see CreateCampaignRequestValidator's own comment.
        RuleFor(x => x.ScheduledStartAt)
            .MustAsync(async (value, cancellationToken) => value!.Value > await tenantTimeZone.GetLocalNowAsync(cancellationToken))
            .When(x => x.ScheduledStartAt.HasValue)
            .WithMessage("Scheduled start must be in the future.");
    }
}

public class UpsertCampaignStepRequestValidator : AbstractValidator<UpsertCampaignStepRequest>
{
    public UpsertCampaignStepRequestValidator()
    {
        RuleFor(x => x.StepType)
            .Must(s => CampaignStepTypeName.TryParse(s, out _))
            .WithMessage("StepType must be 'Initial' or 'FollowUp' followed by a positive number, e.g. 'FollowUp1'.");

        RuleFor(x => x.MessageText)
            .NotEmpty()
            .MaximumLength(2000)
            .Must(body => TemplatePlaceholderResolver.TryValidateTokens(body, out _))
            .WithMessage($"Message text may only use these placeholders: {{{{{string.Join("}}, {{", TemplatePlaceholderResolver.KnownTokens)}}}}}.");

        RuleFor(x => x.DelayDaysAfterPrevious)
            .GreaterThanOrEqualTo(0)
            .When(x => CampaignStepTypeName.TryParse(x.StepType, out var n) && n > 0)
            .WithMessage("Follow-up steps need a non-negative delay.");

        RuleFor(x => x.DelayDaysAfterPrevious)
            .Equal(0)
            .When(x => CampaignStepTypeName.TryParse(x.StepType, out var n) && n == 0)
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
