using FluentValidation;
using WhatsAppSalesAutomation.Application.Billing;

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

public class UpdateTenantCountryRequestValidator : AbstractValidator<UpdateTenantCountryRequest>
{
    public UpdateTenantCountryRequestValidator()
    {
        RuleFor(x => x.CountryCode)
            .NotEmpty()
            .Must(RegionalPricingCatalog.IsValidCode)
            .WithMessage("Country must be one of the platform's supported, priced countries.");
    }
}
