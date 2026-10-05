using FluentValidation;

namespace WhatsAppSalesAutomation.Application.SocialAds;

public class CompleteSocialAdsConnectRequestValidator : AbstractValidator<CompleteSocialAdsConnectRequest>
{
    public CompleteSocialAdsConnectRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.State).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.RedirectUri).NotEmpty().MaximumLength(500)
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback))
            .WithMessage("The redirect address must be an https address.");
    }
}

public class SelectSocialAdAccountRequestValidator : AbstractValidator<SelectSocialAdAccountRequest>
{
    public SelectSocialAdAccountRequestValidator()
    {
        RuleFor(x => x.AdAccountId).NotEmpty().MaximumLength(40);
    }
}

public class SaveManualAdSpendRequestValidator : AbstractValidator<SaveManualAdSpendRequest>
{
    public SaveManualAdSpendRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0).LessThanOrEqualTo(1_000_000_000m).PrecisionScale(18, 2, false);
        RuleFor(x => x.Month).GreaterThan(new DateTime(2015, 1, 1)).WithMessage("Choose a month from 2015 onwards.");
    }
}
