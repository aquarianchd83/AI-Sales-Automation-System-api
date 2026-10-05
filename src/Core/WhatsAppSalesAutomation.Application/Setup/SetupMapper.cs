using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Setup;

namespace WhatsAppSalesAutomation.Application.Setup;

/// <summary>Requirement rows -> the DTO shapes shared by the Talent wizard, the admin editor and the preview.</summary>
public static class SetupMapper
{
    public static SetupFieldDto ToDto(PlanRequirement r) => new(
        r.Id,
        r.FieldKey,
        r.Label,
        r.HelpText,
        r.FieldType,
        r.IsRequired,
        r.DefaultValue,
        SetupJson.ParseOptions(r.OptionsJson),
        SetupJson.ParseValidation(r.ValidationJson),
        r.DisplayOrder,
        r.Section,
        string.IsNullOrWhiteSpace(r.ConditionFieldKey) || r.ConditionOperator is null
            ? null
            : new SetupConditionDto(r.ConditionFieldKey, r.ConditionOperator.Value, r.ConditionValue),
        r.MetricKey,
        r.IsActive);

    /// <summary>The wizard definition: ACTIVE fields only, grouped into sections ordered by the recommended step order.
    /// A section with no active field simply does not exist, so a plan that does not need it never shows it.</summary>
    public static SetupDefinitionDto BuildDefinition(Plan plan, PlanSetupVersion version, IEnumerable<PlanRequirement> requirements)
    {
        var sections = requirements
            .Where(r => r.IsActive)
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Label)
            .GroupBy(r => r.Section, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var info = SetupSections.Describe(g.Key);
                return new SetupSectionDto(g.Key, info.Title, info.Description, info.Order, g.Select(ToDto).ToList());
            })
            .OrderBy(s => s.Order).ThenBy(s => s.Title)
            .ToList();

        return new SetupDefinitionDto(plan.Id, plan.Code, plan.Name, version.Id, version.VersionNumber, sections);
    }

    public static string VersionLabel(Plan plan, PlanSetupVersion version) => $"{plan.Name} v{version.VersionNumber}";
}
