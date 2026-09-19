using WhatsAppSalesAutomation.Application.Billing;
using FluentValidation;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Application.Auth;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class TenantSignUpRequestValidator : AbstractValidator<TenantSignUpRequest>
{
    public TenantSignUpRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug)
            .Matches("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")
            .When(x => !string.IsNullOrWhiteSpace(x.Slug))
            .WithMessage("Slug must be lowercase letters, digits and hyphens only.");
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        RuleFor(x => x.ProductName).MaximumLength(TenantProfileLimits.ProductName);
        // Loose on purpose - an unmatched code just falls back to USD display
        // (RegionalPricingCatalog.Resolve), never an error, so this only guards the shape.
        RuleFor(x => x.CountryCode).Length(2).When(x => x.CountryCode is not null);
        RuleFor(x => x.StateCode)
            .Must(IndianStates.IsValidCode)
            .When(x => !string.IsNullOrWhiteSpace(x.StateCode))
            .WithMessage("Choose one of the listed states.");

        // Stricter than CountryCode above: an unrecognized timezone id would silently degrade to IST
        // (ITenantTimeZoneProvider's own fallback), which is a worse failure mode for a field that
        // directly controls when a tenant's campaigns actually send - reject it here instead.
        RuleFor(x => x.Timezone)
            .Must(TimeZoneCatalog.IsValidId)
            .When(x => x.Timezone is not null)
            .WithMessage("Timezone must be one of the platform's supported timezone ids.");
    }
}

public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8);
    }
}
