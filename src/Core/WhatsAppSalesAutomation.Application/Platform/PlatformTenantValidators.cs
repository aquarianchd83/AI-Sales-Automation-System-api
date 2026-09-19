using FluentValidation;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Mirrors TenantSignUpRequestValidator's rules - same shape of request, just operator-
/// initiated instead of self-serve - plus the optional business details, validated exactly like the
/// tenant's own Business Profile.</summary>
public class CreatePlatformTenantRequestValidator : AbstractValidator<CreatePlatformTenantRequest>
{
    public CreatePlatformTenantRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug)
            .Matches("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")
            .When(x => !string.IsNullOrWhiteSpace(x.Slug))
            .WithMessage("Slug must be lowercase letters, digits and hyphens only.");
        RuleFor(x => x.AdminFullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AdminEmail).NotEmpty().EmailAddress();
        RuleFor(x => x.AdminPassword).NotEmpty().MinimumLength(8);

        // An operator picks these from the console's own lists, so an unsupported value is a mistake
        // to reject rather than a best-effort input to fall back from (unlike signup's CountryCode).
        RuleFor(x => x.CountryCode)
            .Must(code => RegionalPricingCatalog.IsValidCode(code!))
            .When(x => !string.IsNullOrWhiteSpace(x.CountryCode))
            .WithMessage("Country must be one of the platform's supported, priced countries.");
        RuleFor(x => x.StateCode)
            .Must(IndianStates.IsValidCode)
            .When(x => !string.IsNullOrWhiteSpace(x.StateCode))
            .WithMessage("Choose one of the listed states.");
        RuleFor(x => x.Timezone)
            .Must(TimeZoneCatalog.IsValidId)
            .When(x => !string.IsNullOrWhiteSpace(x.Timezone))
            .WithMessage("Timezone must be one of the platform's supported timezone ids.");

        Include(new TenantBusinessDetailsValidator());
    }
}
