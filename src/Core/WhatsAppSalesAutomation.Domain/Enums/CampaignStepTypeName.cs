using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// Formats and parses a campaign step's display name from its position. Replaces what was a
/// closed CampaignStepType enum (Initial + FollowUp1-4 only) - a campaign may now carry any
/// number of follow-ups, so the name is derived from StepNumber rather than a fixed set of
/// members. 0 is always "Initial"; every StepNumber above 0 is "FollowUp{N}".
/// </summary>
public static class CampaignStepTypeName
{
    public const string Initial = "Initial";

    private static readonly Regex FollowUpPattern = new(@"^FollowUp(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ForNumber(int stepNumber) =>
        stepNumber <= 0 ? Initial : $"FollowUp{stepNumber}";

    /// <summary>"Initial" (case-insensitive) parses to 0; "FollowUp{N}" (N a positive integer,
    /// case-insensitive prefix) parses to N. Anything else fails.</summary>
    public static bool TryParse(string? value, out int stepNumber)
    {
        stepNumber = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (string.Equals(value, Initial, StringComparison.OrdinalIgnoreCase))
            return true;

        var match = FollowUpPattern.Match(value.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out stepNumber) && stepNumber > 0;
    }
}
