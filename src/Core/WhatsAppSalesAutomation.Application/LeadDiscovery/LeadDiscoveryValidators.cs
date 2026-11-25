using FluentValidation;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery;

public class SaveLeadDiscoveryProfileRequestValidator : AbstractValidator<SaveLeadDiscoveryProfileRequest>
{
    public SaveLeadDiscoveryProfileRequestValidator()
    {
        RuleFor(x => x.TargetBusinessType).NotEmpty().MaximumLength(LeadDiscoveryLimits.TargetBusinessType);
        RuleFor(x => x.Description).MaximumLength(LeadDiscoveryLimits.Description);

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

        // Existence/active-status of the campaign itself is checked in LeadDiscoveryService.SaveProfileAsync
        // (needs a DB round trip, same as the BatchSize-vs-plan-limit check there) - this only guards the
        // syntactic "enabled with nothing selected" case the UI itself should already be preventing.
        RuleFor(x => x.SourceCampaignId)
            .NotNull()
            .When(x => x.AutoCampaignEnabled)
            .WithMessage("Select a source campaign to enable auto campaign.");

        RuleFor(x => x.AutoCampaignStartMode)
            .Must(m => Enum.TryParse<LeadDiscoveryCampaignStartMode>(m, ignoreCase: true, out _))
            .WithMessage("Auto campaign start must be Immediate or NextDayWithTime.");

        // Reminders: the first at day 1 to 13, the second (optional) after it. How far they may go also depends on the Auto-Campaign's length -
        // checked in LeadDiscoveryService.SaveProfileAsync, which can read the campaign.
        RuleFor(x => x.ConsentReminderFirstDays)
            .InclusiveBetween(1, Consent.ConsentReminderPolicy.MaxReminderDay)
            .When(x => x.ConsentReminderEnabled)
            .WithMessage($"Send the first reminder between day 1 and day {Consent.ConsentReminderPolicy.MaxReminderDay}.");
        RuleFor(x => x.ConsentReminderSecondDays)
            .Must((x, second) => second is null || (second > x.ConsentReminderFirstDays && second <= Consent.ConsentReminderPolicy.MaxReminderDay))
            .When(x => x.ConsentReminderEnabled)
            .WithMessage($"Send the second reminder after the first, and by day {Consent.ConsentReminderPolicy.MaxReminderDay}.");

        RuleFor(x => x.AutoCampaignStartTime)
            .NotNull()
            .When(x => string.Equals(x.AutoCampaignStartMode, nameof(LeadDiscoveryCampaignStartMode.NextDayWithTime), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Pick a time for the next-day start.");
    }

    private static bool HaveAValue(IReadOnlyList<string>? values) =>
        values is not null && values.Any(v => !string.IsNullOrWhiteSpace(v));
}
