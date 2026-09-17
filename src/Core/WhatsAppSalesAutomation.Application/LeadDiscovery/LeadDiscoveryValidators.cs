using FluentValidation;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

public class SaveLeadDiscoveryProfileRequestValidator : AbstractValidator<SaveLeadDiscoveryProfileRequest>
{
    public SaveLeadDiscoveryProfileRequestValidator()
    {
        RuleFor(x => x.TargetBusinessType).NotEmpty().MaximumLength(LeadDiscoveryLimits.TargetBusinessType);

        RuleFor(x => x.Keywords)
            .Must(HaveAValue).WithMessage("Add at least one search keyword.")
            .Must(k => k is null || k.Count <= LeadDiscoveryLimits.MaxKeywords)
            .WithMessage($"Add at most {LeadDiscoveryLimits.MaxKeywords} keywords.");
        RuleForEach(x => x.Keywords).MaximumLength(LeadDiscoveryLimits.Keyword);

        RuleFor(x => x.Locations)
            .Must(HaveAValue).WithMessage("Add at least one location.")
            .Must(l => l is null || l.Count <= LeadDiscoveryLimits.MaxLocations)
            .WithMessage($"Add at most {LeadDiscoveryLimits.MaxLocations} locations.");
        RuleForEach(x => x.Locations).MaximumLength(LeadDiscoveryLimits.Location);

        RuleFor(x => x.BatchSize).InclusiveBetween(1, LeadDiscoveryLimits.MaxBatchSize);

        RuleForEach(x => x.RequiredFields)
            .Must(field => LeadDiscoveryFields.Canonical(field) is not null)
            .WithMessage($"Required fields must be among: {string.Join(", ", LeadDiscoveryFields.All)}.");

        RuleFor(x => x.MinimumLeadScore).InclusiveBetween(0, 100);

        RuleFor(x => x.AdditionalCriteria)
            .Must(c => c is null || c.Count <= LeadDiscoveryLimits.MaxAdditionalCriteria)
            .WithMessage($"Add at most {LeadDiscoveryLimits.MaxAdditionalCriteria} additional criteria.");
        RuleForEach(x => x.AdditionalCriteria).MaximumLength(LeadDiscoveryLimits.Criterion);
    }

    private static bool HaveAValue(IReadOnlyList<string>? values) =>
        values is not null && values.Any(v => !string.IsNullOrWhiteSpace(v));
}
