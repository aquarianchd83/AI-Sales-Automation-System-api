using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Leads.FollowUps;

public class ScheduleLeadFollowUpRequestValidator : AbstractValidator<ScheduleLeadFollowUpRequest>
{
    public ScheduleLeadFollowUpRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.Months.HasValue ^ x.DueAt.HasValue)
            .WithName("Months")
            .WithMessage("Choose either a number of months or an exact date, not both and not neither.");

        RuleFor(x => x.Months)
            .InclusiveBetween(1, LeadFollowUpPolicy.MaxMonths)
            .When(x => x.Months.HasValue);

        RuleFor(x => x.MessageTemplateId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
