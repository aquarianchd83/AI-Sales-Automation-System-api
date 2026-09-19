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

        RuleFor(x => x.StateCode)
            .Must(IndianStates.IsValidCode)
            .When(x => !string.IsNullOrWhiteSpace(x.StateCode))
            .WithMessage("Choose one of the listed states.");
    }
}

/// <summary>Column sizes for the business profile - mirrored by TenantConfiguration and the Angular form.</summary>
public static class TenantProfileLimits
{
    public const int CompanyName = 200;
    public const int ProductName = 200;
    public const int Industry = 100;
    public const int BusinessDescription = 2000;
    public const int WebsiteUrl = 300;
    public const int SupportEmail = 256;
    public const int SupportPhone = 32;
    public const int Keyword = 50;
    public const int MaxKeywords = 30;
}

public class UpdateTenantBusinessProfileRequestValidator : AbstractValidator<UpdateTenantBusinessProfileRequest>
{
    public UpdateTenantBusinessProfileRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(TenantProfileLimits.CompanyName);
        Include(new TenantBusinessDetailsValidator());
    }
}
