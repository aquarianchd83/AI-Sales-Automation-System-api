using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Handoffs;

public class ResolveHandoffRequestValidator : AbstractValidator<ResolveHandoffRequest>
{
    public ResolveHandoffRequestValidator()
    {
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}
