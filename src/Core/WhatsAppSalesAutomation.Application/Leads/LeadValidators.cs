using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads;

public class UpdateLeadRequestValidator : AbstractValidator<UpdateLeadRequest>
{
    public UpdateLeadRequestValidator()
    {
        RuleFor(x => x.Stage)
            .Must(s => Enum.TryParse<LeadStage>(s, ignoreCase: true, out _))
            .WithMessage($"Stage must be one of: {string.Join(", ", Enum.GetNames<LeadStage>())}.")
            .When(x => x.Stage is not null);

        RuleFor(x => x.Budget).MaximumLength(200);
        RuleFor(x => x.Interest).MaximumLength(500);
        RuleFor(x => x.PurchaseTimeline).MaximumLength(200);
    }
}

public class AddLeadActivityRequestValidator : AbstractValidator<AddLeadActivityRequest>
{
    public AddLeadActivityRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().MaximumLength(2000);
    }
}
