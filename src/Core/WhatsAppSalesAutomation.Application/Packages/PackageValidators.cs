using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Packages;

public class SavePackageRequestValidator : AbstractValidator<SavePackageRequest>
{
    public const int MaxFeatures = 20;
    public const int MaxFeatureLength = 200;

    public SavePackageRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).LessThanOrEqualTo(1_000_000_000m).PrecisionScale(18, 2, false);
        RuleFor(x => x.DurationValue).InclusiveBetween(1, 1000);
        RuleFor(x => x.DurationUnit).IsInEnum();
        RuleFor(x => x.ExpectedSales).InclusiveBetween(0, 1_000_000);
        RuleFor(x => x.Features)
            .Must(f => f is null || f.Count(s => !string.IsNullOrWhiteSpace(s)) <= MaxFeatures)
            .WithMessage($"A package can list at most {MaxFeatures} features.");
        RuleForEach(x => x.Features)
            .Must(s => s is null || s.Trim().Length <= MaxFeatureLength)
            .WithMessage($"A feature can be at most {MaxFeatureLength} characters.");
    }
}
