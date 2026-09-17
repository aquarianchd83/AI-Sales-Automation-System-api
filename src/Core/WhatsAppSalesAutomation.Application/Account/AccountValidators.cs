using FluentValidation;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Application.Account;

public class UpdateUserProfileRequestValidator : AbstractValidator<UpdateUserProfileRequest>
{
    /// <summary>Same rule as TenantBusinessDetailsValidator.SupportPhone, so the two phone fields in this
    /// application accept the same shapes.</summary>
    private const string PhonePattern = @"^\+?[0-9 ()-]{5,31}$";

    public UpdateUserProfileRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);

        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);

        RuleFor(x => x.PhoneNumber)
            .Matches(PhonePattern)
            .When(x => !string.IsNullOrWhiteSpace(x.PhoneNumber))
            .WithMessage("Use digits, spaces, brackets, dashes and an optional leading +.");

        // Must be one of the curated ids the timezone picker offers, the same check a tenant's own timezone
        // gets - a stored id nothing can show back to whoever set it is worse than a rejected one.
        RuleFor(x => x.Timezone)
            .NotEmpty()
            .Must(TimeZoneCatalog.IsValidId)
            .WithMessage("Choose one of the supported timezones.");

        RuleFor(x => x.CountryCode)
            .Must(RegionalPricingCatalog.IsValidCode)
            .When(x => !string.IsNullOrWhiteSpace(x.CountryCode))
            .WithMessage("Choose one of the supported countries.");
    }
}
