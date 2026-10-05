using System.Text.Json;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Setup;

// ---------------------------------------------------------------------------------------------------------------
// What a plan asks (the definition) - the same shapes drive the Talent wizard, the admin editor and the preview.
// ---------------------------------------------------------------------------------------------------------------

public record SetupOptionDto(string Value, string Label);

/// <summary>Min/Max bound a number's value, or - for a multi-select - how many options may be picked.</summary>
public record SetupValidationDto(decimal? Min, decimal? Max, int? MinLength, int? MaxLength, string? Pattern, string? PatternMessage);

/// <summary>Show the field only when <paramref name="FieldKey"/>'s answer matches. A hidden parent hides its children.</summary>
public record SetupConditionDto(string FieldKey, SetupConditionOperator Operator, string? Value);

public record SetupFieldDto(
    Guid Id,
    string FieldKey,
    string Label,
    string? HelpText,
    SetupFieldType FieldType,
    bool IsRequired,
    string? DefaultValue,
    IReadOnlyList<SetupOptionDto> Options,
    SetupValidationDto? Validation,
    int DisplayOrder,
    string Section,
    SetupConditionDto? Condition,
    string? MetricKey,
    bool IsActive);

public record SetupSectionDto(string Key, string Title, string Description, int Order, IReadOnlyList<SetupFieldDto> Fields);

public record SetupDefinitionDto(
    Guid PlanId,
    string PlanCode,
    string PlanName,
    Guid VersionId,
    int VersionNumber,
    IReadOnlyList<SetupSectionDto> Sections);

// ---------------------------------------------------------------------------------------------------------------
// How far an application's answers get it (the evaluation).
// ---------------------------------------------------------------------------------------------------------------

public record SetupFieldIssueDto(string FieldKey, string Message);

public record SetupSectionProgressDto(string Key, string Title, int RequiredCount, int AnsweredCount, bool Started, bool IsComplete);

/// <param name="Missing">Visible mandatory fields with no answer.</param>
/// <param name="Invalid">Answers that are present but wrong (bad email, number out of range ...).</param>
public record SetupEvaluationDto(
    bool IsComplete,
    int PercentComplete,
    IReadOnlyList<string> VisibleFieldKeys,
    IReadOnlyList<SetupFieldIssueDto> Missing,
    IReadOnlyList<SetupFieldIssueDto> Invalid,
    IReadOnlyList<SetupSectionProgressDto> Sections);

// ---------------------------------------------------------------------------------------------------------------
// Talent-facing application API.
// ---------------------------------------------------------------------------------------------------------------

public record AvailablePlanDto(
    Guid PlanId,
    string Code,
    string Name,
    int PriceMonthlyCents,
    int VersionNumber,
    int FieldCount,
    int RequiredFieldCount,
    IReadOnlyList<string> Sections);

public record CreateApplicationRequest(Guid PlanId, string? Name);

public record ChangePlanRequest(Guid PlanId, string? Reason);

public record ApplicationDto(
    Guid Id,
    string Name,
    Guid PlanId,
    string PlanCode,
    string PlanName,
    Guid PlanSetupVersionId,
    int VersionNumber,
    bool NewerVersionAvailable,
    ApplicationStatus Status,
    ApplicationSetupStatus SetupStatus,
    int SetupPercent,
    DateTime? SetupCompletedAt,
    DateTime? SetupExpiresAt,
    DateTime? LastExecutedAt,
    int ExecutionCount,
    bool CanExecute,
    DateTime CreatedAt);

/// <param name="Values">Answers by field key, typed for the field (text, number, bool, string list), defaults filled in.</param>
public record ApplicationSetupDto(
    ApplicationDto Application,
    SetupDefinitionDto Definition,
    IReadOnlyDictionary<string, object?> Values,
    SetupEvaluationDto Evaluation,
    ApplicationProjectionDto Projection);

/// <param name="Values">Partial: only the keys sent are touched. A JSON null (or empty string/array) clears an answer.</param>
/// <param name="Complete">True from the review step: confirm the setup, if - and only if - it is fully valid.</param>
public record SaveSetupRequest(IReadOnlyDictionary<string, JsonElement>? Values, bool Complete, string? Reason);

/// <param name="Completed">False when completion was asked for but the setup is not valid yet - see Setup.Evaluation.</param>
public record SaveSetupResultDto(bool Completed, int ChangedCount, ApplicationSetupDto Setup);

public record ApplicationExecutionDto(Guid Id, Guid ApplicationId, string PlanVersionLabel, DateTime StartedAt, int FieldCount);

public record SetupAuditEntryDto(
    Guid Id,
    SetupAuditAction Action,
    string? FieldKey,
    string? FieldLabel,
    string? PreviousValue,
    string? NewValue,
    string? Reason,
    string PlanVersionLabel,
    Guid? PerformedBy,
    string? PerformedByName,
    bool ByPlatformSupport,
    DateTime PerformedAt);

/// <summary>What the plan's configuration says the Talent can expect. Every figure is null until the answers it
/// is calculated from exist - never a made-up zero.</summary>
public record ApplicationProjectionDto(
    bool HasData,
    decimal? PackagePrice,
    decimal? ExpectedCustomers,
    decimal? ExpectedLeads,
    decimal? ExpectedRevenue,
    decimal MarketingCost,
    decimal SocialMediaCost,
    decimal OperationalCost,
    decimal TotalCost,
    decimal? ExpectedProfit,
    decimal? RoiPercent,
    IReadOnlyList<string> MissingInputs);

// ---------------------------------------------------------------------------------------------------------------
// Admin (PlatformSuperAdmin) API.
// ---------------------------------------------------------------------------------------------------------------

public record SetupVersionSummaryDto(
    Guid Id,
    int VersionNumber,
    SetupVersionStatus Status,
    string? ReleaseNotes,
    int? ValidityDays,
    int RequirementCount,
    int ApplicationCount,
    DateTime? PublishedAt,
    DateTime CreatedAt);

public record PlanSetupSummaryDto(
    Guid PlanId,
    string PlanCode,
    string PlanName,
    bool PlanIsActive,
    IReadOnlyList<SetupVersionSummaryDto> Versions);

public record SetupSectionInfoDto(string Key, string Title, string Description, int Order);

/// <param name="Fields">Every requirement of the version, inactive ones included - the editor shows them dimmed.</param>
public record SetupVersionDetailDto(
    Guid Id,
    Guid PlanId,
    string PlanCode,
    string PlanName,
    int VersionNumber,
    SetupVersionStatus Status,
    string? ReleaseNotes,
    int? ValidityDays,
    DateTime? PublishedAt,
    int ApplicationCount,
    IReadOnlyList<SetupFieldDto> Fields,
    IReadOnlyList<SetupSectionInfoDto> Sections,
    IReadOnlyList<string> MetricKeys);

public record CreateSetupVersionRequest(Guid? CloneFromVersionId, string? ReleaseNotes);

public record SaveSetupVersionRequest(string? ReleaseNotes, int? ValidityDays);

public record SaveRequirementRequest(
    string FieldKey,
    string Label,
    string? HelpText,
    SetupFieldType FieldType,
    bool IsRequired,
    string? DefaultValue,
    IReadOnlyList<SetupOptionDto>? Options,
    SetupValidationDto? Validation,
    int DisplayOrder,
    string Section,
    SetupConditionDto? Condition,
    string? MetricKey,
    bool IsActive);
