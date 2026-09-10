using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Mirrors TenantSignUpRequestValidator's rules - same shape of request, just operator-
/// initiated instead of self-serve.</summary>
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
    }
}
