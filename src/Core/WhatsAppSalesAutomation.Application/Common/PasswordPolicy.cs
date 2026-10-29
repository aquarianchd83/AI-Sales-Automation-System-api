using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>
/// The password rules ASP.NET Identity enforces (see Infrastructure's AddIdentityCore options), stated up front so a
/// weak password is refused by the request validator instead of failing inside <c>UserManager.CreateAsync</c> - which,
/// for the flows that create a tenant first and its admin second, used to leave an orphan tenant behind on every retry.
/// Keep in step with those options.
/// </summary>
public static class PasswordPolicy
{
    public const string Requirements = "Password must be at least 8 characters and include an uppercase letter, a lowercase letter, a number and a symbol.";

    public static IRuleBuilderOptions<T, string> MeetsPasswordPolicy<T>(this IRuleBuilder<T, string> rule)
        => rule
            .NotEmpty()
            .MinimumLength(8).WithMessage(Requirements)
            .Matches("[A-Z]").WithMessage(Requirements)
            .Matches("[a-z]").WithMessage(Requirements)
            .Matches("[0-9]").WithMessage(Requirements)
            .Matches("[^a-zA-Z0-9]").WithMessage(Requirements);
}
