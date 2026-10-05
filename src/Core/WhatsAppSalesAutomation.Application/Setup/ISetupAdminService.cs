namespace WhatsAppSalesAutomation.Application.Setup;

/// <summary>Platform-admin management of each plan's setup requirements: versions, their fields, and publishing.
/// Only a Draft version can be edited; publishing makes it what NEW applications on the plan use, while existing
/// applications stay on the version they were created with until explicitly migrated.</summary>
public interface ISetupAdminService
{
    Task<IReadOnlyList<PlanSetupSummaryDto>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<SetupVersionDetailDto> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Exactly what a Talent would be shown for this version - active fields only.</summary>
    Task<SetupDefinitionDto> GetPreviewAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Starts the next Draft for the plan, copying the latest published version (or <c>CloneFromVersionId</c>).</summary>
    Task<SetupVersionDetailDto> CreateVersionAsync(Guid planId, CreateSetupVersionRequest request, CancellationToken cancellationToken = default);

    Task<SetupVersionDetailDto> UpdateVersionAsync(Guid versionId, SaveSetupVersionRequest request, CancellationToken cancellationToken = default);

    Task<SetupVersionDetailDto> PublishVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    Task DeleteVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    Task<SetupFieldDto> AddRequirementAsync(Guid versionId, SaveRequirementRequest request, CancellationToken cancellationToken = default);

    Task<SetupFieldDto> UpdateRequirementAsync(Guid requirementId, SaveRequirementRequest request, CancellationToken cancellationToken = default);

    Task DeleteRequirementAsync(Guid requirementId, CancellationToken cancellationToken = default);
}
