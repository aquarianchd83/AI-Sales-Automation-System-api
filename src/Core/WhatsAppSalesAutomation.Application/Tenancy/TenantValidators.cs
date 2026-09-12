using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Tenancy;

public class UpdateTenantTimezoneRequestValidator : AbstractValidator<UpdateTenantTimezoneRequest>
{
    public UpdateTenantTimezoneRequestValidator()
    {
        RuleFor(x => x.Timezone)
            .NotEmpty()
            .Must(TimeZoneCatalog.IsValidId)
            .WithMessage("Timezone must be one of the platform's supported timezone ids.");
    }
}
