using System.Globalization;
using FluentValidation;

namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

public class UpdateTenantWhatsAppConfigRequestValidator : AbstractValidator<UpdateTenantWhatsAppConfigRequest>
{
    public UpdateTenantWhatsAppConfigRequestValidator()
    {
        RuleFor(x => x.PhoneNumberId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.WhatsAppBusinessAccountId).NotEmpty().MaximumLength(64);
    }
}


/// <summary>Format-only validation for the tenant config-overrides request - whether a key is even
/// tenant-overridable at all is TenantConfigOverrideProvider's job (NotFoundException), same split
/// SettingsService/the two validators above already use between catalog/membership rules and
/// request-shape rules.</summary>
public class UpdateTenantSettingsRequestValidator : AbstractValidator<UpdateTenantSettingsRequest>
{
    private static readonly Dictionary<string, (Func<string, bool> IsValid, string Message)> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Campaigns:MinStepMedia"] = (IsInt, "must be a whole number"),
        ["Campaigns:MaxStepMedia"] = (IsInt, "must be a whole number"),
        ["Media:MaxSizeBytes"] = (IsLong, "must be a whole number"),
        ["Media:AllowedContentTypes"] = (IsNonEmptyList, "must be a comma-separated list of at least one value"),
        ["Messaging:MaxSendsPerRun"] = (IsInt, "must be a whole number"),
        ["Messaging:MaxRetryAttempts"] = (IsInt, "must be a whole number"),
        ["Messaging:RetryBackoffMinutes"] = (IsIntList, "must be a comma-separated list of whole numbers"),
        ["Messaging:CustomerServiceWindowHours"] = (IsInt, "must be a whole number"),
        ["Ai:ConfidenceThreshold"] = (IsUnitInterval, "must be a number between 0 and 1"),
        ["Ai:EscalationIntents"] = (IsNonEmptyList, "must be a comma-separated list of at least one value"),
        ["Ai:KnowledgeBaseTopN"] = (IsInt, "must be a whole number"),
        ["Ai:MinRelevanceScore"] = (IsUnitInterval, "must be a number between 0 and 1"),
        ["Ai:ConversationHistoryTurns"] = (IsInt, "must be a whole number"),
        ["Ai:MinFieldExtractionConfidence"] = (IsUnitInterval, "must be a number between 0 and 1"),
        ["Ai:MaxFieldsToAsk"] = (IsInt, "must be a whole number"),
        ["Ai:HandoffOnHotLead"] = (v => IsOneOf(v, "true", "false"), "must be true or false"),
        ["ContentStudio:Enabled"] = (v => IsOneOf(v, "true", "false"), "must be true or false"),
        ["ContentStudio:StorageMode"] = (v => IsOneOf(v, "PublicBucket", "SignedUrls"), "must be PublicBucket or SignedUrls"),
        ["ContentStudio:StorageKeyPrefix"] = (IsFolderName, "must be a folder name of letters, digits, - or _"),
        ["ContentStudio:SignedUrlMinutes"] = (IsMinutes, "must be a whole number of minutes between 1 and 1440"),
    };

    public UpdateTenantSettingsRequestValidator()
    {
        RuleForEach(x => x.Values).Custom((entry, context) =>
        {
            // Blank clears the override, whatever the key - always valid.
            if (string.IsNullOrWhiteSpace(entry.Value))
                return;

            if (Rules.TryGetValue(entry.Key, out var rule) && !rule.IsValid(entry.Value))
                context.AddFailure(entry.Key, $"'{entry.Key}' {rule.Message}.");
        });
    }

    private static bool IsOneOf(string value, params string[] allowed) => allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool IsFolderName(string value) =>
        value.Trim().Length is > 0 and <= 40 && value.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsMinutes(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 1440;

    private static bool IsInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static bool IsLong(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static bool IsUnitInterval(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 1;

    private static bool IsNonEmptyList(string value) => value.Split(',').Any(part => !string.IsNullOrWhiteSpace(part));

    private static bool IsIntList(string value)
    {
        var parts = value.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
        return parts.Count > 0 && parts.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }
}
