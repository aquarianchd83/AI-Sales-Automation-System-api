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
    public const int IndustrySubcategory = 100;
    public const int BusinessDescription = 2000;
    public const int WebsiteUrl = 300;
    public const int SupportEmail = 256;
    public const int SupportPhone = 32;
    public const int WorkingHours = 500;
    public const int TargetAudience = 1000;
    public const int TargetLocation = 500;
    public const int TargetCustomerType = 50;
    public const int Keyword = 50;
    public const int MaxKeywords = 30;
    public const int WhatsAppNumber = 32;
}

public class UpdateTenantWhatsAppNumberRequestValidator : AbstractValidator<UpdateTenantWhatsAppNumberRequest>
{
    public UpdateTenantWhatsAppNumberRequestValidator()
    {
        RuleFor(x => x.WhatsAppNumber)
            .MaximumLength(TenantProfileLimits.WhatsAppNumber)
            // A phone number: digits, with spaces, brackets and dashes allowed and an optional leading +; at least 7 digits.
            .Matches(@"^\+?[0-9 ()\-]{5,31}$")
            .WithMessage("Enter the number with digits only, a leading + and the country code, e.g. +91 98765 43210.")
            .Must(n => n!.Count(char.IsDigit) >= 7)
            .WithMessage("That number looks too short - include the country code.")
            .When(x => !string.IsNullOrWhiteSpace(x.WhatsAppNumber));
    }
}

public class UpdateTenantBusinessProfileRequestValidator : AbstractValidator<UpdateTenantBusinessProfileRequest>
{
    public UpdateTenantBusinessProfileRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(TenantProfileLimits.CompanyName);
        Include(new TenantBusinessDetailsValidator());
        RuleFor(x => x.WorkingHours).MaximumLength(TenantProfileLimits.WorkingHours);
        RuleFor(x => x.IndustrySubcategory).MaximumLength(TenantProfileLimits.IndustrySubcategory);
        RuleFor(x => x.TargetAudience).MaximumLength(TenantProfileLimits.TargetAudience);
        RuleFor(x => x.TargetLocation).MaximumLength(TenantProfileLimits.TargetLocation);
        RuleFor(x => x.TargetCustomerType).MaximumLength(TenantProfileLimits.TargetCustomerType);
    }
}
