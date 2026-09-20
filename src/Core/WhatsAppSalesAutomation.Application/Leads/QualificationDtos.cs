namespace WhatsAppSalesAutomation.Application.Leads;

/// <summary>One configured qualification field as the admin UI and the prompt builder see it.
/// AllowedValues arrives as a list rather than the stored JSON string - the storage shape is an
/// implementation detail the API should not leak.</summary>
public record QualificationFieldDto(
    Guid Id,
    string FieldKey,
    string DisplayName,
    string? Description,
    string Question,
    string DataType,
    bool IsRequired,
    int Priority,
    int ScoreWeight,
    IReadOnlyList<string>? AllowedValues,
    string? ValidationPattern,
    bool IsActive,
    int SortOrder,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    /// <summary>How many leads currently have an accepted value for this field. Shown in the admin UI
    /// so someone about to deactivate a field can see what it would stop collecting.</summary>
    int CapturedLeadCount);

/// <summary>FieldKey is settable only on create - it is what already-captured values are stored
/// against, so changing it would orphan them. Rename DisplayName instead.</summary>
public record CreateQualificationFieldRequest(
    string FieldKey,
    string DisplayName,
    string? Description,
    string Question,
    string DataType,
    bool IsRequired,
    int Priority,
    int ScoreWeight,
    IReadOnlyList<string>? AllowedValues,
    string? ValidationPattern,
    int SortOrder);

public record UpdateQualificationFieldRequest(
    string DisplayName,
    string? Description,
    string Question,
    string DataType,
    bool IsRequired,
    int Priority,
    int ScoreWeight,
    IReadOnlyList<string>? AllowedValues,
    string? ValidationPattern,
    int SortOrder);

/// <summary>Reorders in one call rather than one PUT per field - dragging a list into a new order is
/// a single user action and should be a single request, not N racing ones.</summary>
public record ReorderQualificationFieldsRequest(IReadOnlyList<Guid> OrderedIds);

/// <summary>One captured answer, for the lead detail screen.</summary>
public record LeadQualificationValueDto(
    Guid Id,
    string FieldKey,
    string DisplayName,
    string RawValue,
    string? NormalizedValue,
    double ExtractionConfidence,
    /// <summary>False when the AI extracted it, true when a human entered or corrected it.</summary>
    bool EnteredByHuman,
    Guid? CapturedFromMessageId,
    DateTime CapturedAt);

/// <summary>The lead's whole qualification picture: what is known, what is still missing, and how far
/// along it is. Shown on the lead screen and used by the agent to decide what to ask next.</summary>
public record LeadQualificationDto(
    Guid LeadId,
    IReadOnlyList<LeadQualificationValueDto> Captured,
    IReadOnlyList<QualificationFieldDto> Missing,
    bool AllRequiredCaptured,
    int CapturedCount,
    int TotalCount);

/// <summary>A human correcting or supplying a value on the lead screen. Stored with
/// ExtractionConfidence 1.0 and CapturedByUserId set, which also means it is never re-asked.</summary>
public record SetLeadQualificationValueRequest(string Value);
