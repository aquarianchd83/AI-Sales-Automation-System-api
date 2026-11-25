using FluentValidation;
using WhatsAppSalesAutomation.Application.Billing;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Platform;

public class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    public CreatePlanRequestValidator(IApplicationDbContext context)
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(40)
            .Matches("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")
            .WithMessage("Code must be lowercase letters, digits and hyphens only, e.g. \"starter\".")
            .MustAsync(async (code, cancellationToken) =>
                !await context.Plans.AnyAsync(p => p.Code == code, cancellationToken))
            .WithMessage("A plan with this code already exists.");

        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxUsers).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxMessagesPerMonth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxCampaigns).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxCustomers).GreaterThanOrEqualTo(0).When(x => x.MaxCustomers is not null);
        RuleFor(x => x.MaxKnowledgeBaseArticles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxLeadDiscoveryBatchSize).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceMonthlyCents).GreaterThanOrEqualTo(0);
        RuleFor(x => x.StarterCredits).InclusiveBetween(0m, PlanQuotaListValidator.MaxUnits);
        RuleFor(x => x.MonthlyCredits).InclusiveBetween(0m, PlanQuotaListValidator.MaxUnits);
        RuleForEach(x => x.Features).IsInEnum();
        RuleFor(x => x.CountryPrices).SetValidator(new CountryPriceListValidator()!);
        RuleFor(x => x.FreeTrials).SetValidator(new PlanFeatureTrialListValidator()!);
    }
}

public class UpdatePlanRequestValidator : AbstractValidator<UpdatePlanRequest>
{
    public UpdatePlanRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxUsers).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxMessagesPerMonth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxCampaigns).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxCustomers).GreaterThanOrEqualTo(0).When(x => x.MaxCustomers is not null);
        RuleFor(x => x.MaxKnowledgeBaseArticles).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxLeadDiscoveryBatchSize).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PriceMonthlyCents).GreaterThanOrEqualTo(0);
        RuleFor(x => x.StarterCredits).InclusiveBetween(0m, PlanQuotaListValidator.MaxUnits).When(x => x.StarterCredits is not null);
        RuleFor(x => x.MonthlyCredits).InclusiveBetween(0m, PlanQuotaListValidator.MaxUnits).When(x => x.MonthlyCredits is not null);
        RuleForEach(x => x.Features).IsInEnum();
        RuleFor(x => x.CountryPrices).SetValidator(new CountryPriceListValidator()!);
        RuleFor(x => x.FreeTrials).SetValidator(new PlanFeatureTrialListValidator()!);
    }
}

/// <summary>Free uses of features outside a plan: only features that have a unit to count, a sane number, and no feature twice.</summary>
public class PlanFeatureTrialListValidator : AbstractValidator<IReadOnlyList<PlanFeatureTrialInput>>
{
    public const int MaxFreeUses = 100_000;

    public PlanFeatureTrialListValidator()
    {
        RuleForEach(x => x).ChildRules(trial =>
        {
            trial.RuleFor(t => t.Feature).IsInEnum()
                .Must(f => WhatsAppSalesAutomation.Application.Credits.PlatformFeatureCatalog.TrialUnitFor(f) is not null)
                .WithMessage("This feature cannot be given free uses.");
            trial.RuleFor(t => t.FreeUses).InclusiveBetween(0, MaxFreeUses).WithMessage("Free uses must be between 0 and 100,000.");
        });

        RuleFor(x => x)
            .Must(list => list.Select(t => t.Feature).Distinct().Count() == list.Count)
            .WithMessage("Each feature can be listed only once.");
    }
}

/// <summary>Per-country prices: only countries the platform prices (Billing.RegionalPricingCatalog), a sane
/// amount, and no country listed twice. 0 is allowed and means "remove this country's price".</summary>
public class CountryPriceListValidator : AbstractValidator<IReadOnlyList<CountryPriceInput>>
{
    public const decimal MaxAmount = 1_000_000_000m;

    public CountryPriceListValidator()
    {
        RuleForEach(x => x).ChildRules(price =>
        {
            price.RuleFor(p => p.CountryCode).Must(RegionalPricingCatalog.IsValidCode).WithMessage("Country is not one the platform prices for.");
            price.RuleFor(p => p.Amount).InclusiveBetween(0, MaxAmount).WithMessage("Price must be between 0 and 1,000,000,000.");
        });

        RuleFor(x => x)
            .Must(list => list.Select(p => p.CountryCode?.ToUpperInvariant()).Distinct().Count() == list.Count)
            .WithMessage("Each country can be listed only once.");
    }
}

/// <summary>The quota list on a plan: each amount sane, and no quota type listed twice (which row would win?).</summary>
public class PlanQuotaListValidator : AbstractValidator<IReadOnlyList<PlanQuotaInput>>
{
    public const decimal MaxUnits = 1_000_000_000m;

    public PlanQuotaListValidator()
    {
        RuleForEach(x => x).ChildRules(quota =>
            quota.RuleFor(q => q.Units).InclusiveBetween(0, MaxUnits).WithMessage("Included units must be between 0 and 1,000,000,000."));

        RuleFor(x => x)
            .Must(list => list.Select(q => q.QuotaType).Distinct().Count() == list.Count)
            .WithMessage("Each quota type can be listed only once.");
    }
}

public class CreateCreditPackRequestValidator : AbstractValidator<CreateCreditPackRequest>
{
    public CreateCreditPackRequestValidator()
    {
        RuleFor(x => x.QuotaType).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Units).GreaterThan(0).LessThanOrEqualTo(PlanQuotaListValidator.MaxUnits);
        RuleFor(x => x.PriceCents).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CountryPrices).SetValidator(new CountryPriceListValidator()!);
    }
}

public class UpdateCreditPackRequestValidator : AbstractValidator<UpdateCreditPackRequest>
{
    public UpdateCreditPackRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Units).GreaterThan(0).LessThanOrEqualTo(PlanQuotaListValidator.MaxUnits);
        RuleFor(x => x.PriceCents).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CountryPrices).SetValidator(new CountryPriceListValidator()!);
    }
}
