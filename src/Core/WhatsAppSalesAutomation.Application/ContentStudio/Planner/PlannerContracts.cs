using WhatsAppSalesAutomation.Application.ContentStudio.Assets;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Planner;

public record CreateProjectRequest(string Title, string Goal, string? Language);

public record UpdateProjectRequest(string? Title, string? Goal, string? Language);

public record GenerateTopicsRequest(int? Count);

/// <param name="Status">"Suggested", "Chosen" or "Rejected". Choosing a topic un-chooses the one chosen before.</param>
public record UpdateTopicRequest(string? Title, string? Status);

/// <param name="TopicId">The topic to write about; the chosen one when null.</param>
public record GenerateScriptRequest(Guid? TopicId, int? TargetSeconds, string? Notes);

public record UpdateSceneRequest(string Narration, string? OnScreenText, int DurationSeconds, string? VisualHint);

public record ProjectSummaryDto(Guid Id, string Title, string Goal, string Language, string Status, int TopicCount, int VersionCount, string? ChosenTopic, DateTime CreatedAt, DateTime? UpdatedAt);

public record TopicDto(Guid Id, string Title, string? Rationale, string Status, int FactCount, bool IsSimulated, string Provider, DateTime CreatedAt);

/// <summary>A verified fact as cited by a scene, with its current wording.</summary>
public record CitedFactDto(Guid Id, string Category, string Statement);

public record SceneDto(
    Guid Id, int Order, string Narration, string? OnScreenText, int DurationSeconds, string? VisualHint,
    string ClaimStatus, string? ClaimNote, bool EditedByTenant, IReadOnlyList<CitedFactDto> Facts, AssetSummaryDto? Asset);

public record ScriptVersionSummaryDto(Guid Id, int Number, string? TopicTitle, string Status, bool IsSimulated, int SceneCount, int TotalSeconds, DateTime CreatedAt);

/// <param name="Supported">Scenes backed by a verified fact.</param>
/// <param name="Flagged">Scenes with a claim no verified fact backs - the ones the tenant must look at.</param>
/// <param name="ScenesWithoutPicture">Scenes that have no picture chosen yet.</param>
public record ScriptVersionDto(
    Guid Id, Guid ProjectId, int Number, Guid? TopicId, string? TopicTitle, string Status, int TargetSeconds, int TotalSeconds, string? Notes,
    string Provider, string? Model, string PromptVersion, bool IsSimulated, Guid? ActivityId, DateTime CreatedAt,
    int Supported, int Flagged, int ScenesWithoutPicture, IReadOnlyList<SceneDto> Scenes);

/// <param name="UsableFacts">How many verified, current facts the writer has to work from. Nothing can be written without any.</param>
public record ProjectDetailDto(
    Guid Id, string Title, string Goal, string Language, string Status, int UsableFacts, DateTime CreatedAt, DateTime? UpdatedAt,
    IReadOnlyList<TopicDto> Topics, IReadOnlyList<ScriptVersionSummaryDto> Versions, ScriptVersionDto? CurrentVersion);

public interface IContentPlannerService
{
    Task<IReadOnlyList<ProjectSummaryDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ProjectDetailDto> CreateAsync(Guid tenantId, CreateProjectRequest request, CancellationToken cancellationToken = default);

    Task<ProjectDetailDto> GetAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectDetailDto> UpdateAsync(Guid tenantId, Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Asks the AI for topic ideas that rest on the tenant's verified facts. Charged one content generation; returned if nothing usable came back.</summary>
    Task<ProjectDetailDto> GenerateTopicsAsync(Guid tenantId, Guid projectId, GenerateTopicsRequest request, CancellationToken cancellationToken = default);

    Task<ProjectDetailDto> UpdateTopicAsync(Guid tenantId, Guid topicId, UpdateTopicRequest request, CancellationToken cancellationToken = default);

    /// <summary>Writes a new script version (scenes included) for a topic. Every scene is then checked, in code, against the verified facts.</summary>
    Task<ProjectDetailDto> GenerateScriptAsync(Guid tenantId, Guid projectId, GenerateScriptRequest request, CancellationToken cancellationToken = default);

    Task<ScriptVersionDto> GetVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);

    Task<ScriptVersionDto> UpdateSceneAsync(Guid tenantId, Guid sceneId, UpdateSceneRequest request, CancellationToken cancellationToken = default);

    Task<ScriptVersionDto> DeleteSceneAsync(Guid tenantId, Guid sceneId, CancellationToken cancellationToken = default);

    /// <summary>Checks every scene again against the facts as they are now (a fact may have been verified, retired or expired since).</summary>
    Task<ScriptVersionDto> RecheckAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);
}
