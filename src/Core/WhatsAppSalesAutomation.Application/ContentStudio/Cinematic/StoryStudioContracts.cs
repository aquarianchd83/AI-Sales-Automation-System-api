using System.Text.Json;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record StoryCharacterDto(
    Guid Id, string Name, string? Description, string? Personality, string? VisualAttributes, string? VoicePreferences,
    StoryCharacterOrigin Origin, StoryCharacterVisibility Visibility, bool IsOwnedByTenant, bool RightsConfirmed,
    Guid? ApprovedReferenceId, int ReferenceCount);

public sealed record StoryReferenceDto(Guid Id, int Version, StoryReferenceStatus Status, string ContentType, long SizeBytes, int? Width, int? Height);

public sealed record CreateStoryCharacterRequest(
    string Name, string? Description, string? Personality, string? VisualAttributes, string? VoicePreferences,
    StoryCharacterOrigin Origin = StoryCharacterOrigin.Uploaded);

public sealed record StoryProjectDto(
    Guid Id, string Title, string Topic, string Language, string? Style, string? Audience, string? Moral, int TargetSeconds,
    StoryProjectStatus Status, SceneVideoProviderKind VideoProvider, decimal? BudgetUsd, Guid? CurrentScriptVersionId,
    IReadOnlyList<Guid> CharacterIds, DateTime CreatedAt);

public sealed record CreateStoryProjectRequest(
    string Title, string Topic, string Language, string? Style, string? Audience, string? Moral, int TargetSeconds,
    SceneVideoProviderKind VideoProvider, decimal? BudgetUsd, IReadOnlyList<Guid> CharacterIds);

public sealed record StoryScriptVersionDto(
    Guid Id, int Number, StoryScriptStatus Status, string Source, string ContentHash, CinematicScript Script, bool IsSimulated, DateTime CreatedAt);

public sealed record SaveStoryScriptRequest(CinematicScript Script);

/// <summary>What generating the whole video would cost, before the user confirms. <see cref="IsKnown"/> false means the provider cannot say.</summary>
public sealed record StoryCostEstimateDto(bool IsKnown, decimal ClipUsd, int ClipSeconds, int Clips, string Provider, string Note);

public sealed record StorySceneDto(Guid Id, int Index, int TargetSeconds, StorySceneStatus Status, string VideoPrompt, IReadOnlyList<StoryDialogueDto> Dialogue);

public sealed record StoryDialogueDto(string Speaker, string Text, int StartMs, int EndMs);

public interface IStoryStudioService
{
    Task<IReadOnlyList<StoryCharacterDto>> ListCharactersAsync(Guid tenantId, string? search, CancellationToken ct = default);

    Task<StoryCharacterDto> CreateCharacterAsync(Guid tenantId, Guid? userId, CreateStoryCharacterRequest request, CancellationToken ct = default);

    Task ArchiveCharacterAsync(Guid tenantId, Guid characterId, CancellationToken ct = default);

    /// <summary>Stores an uploaded reference image after checking what it really is. <paramref name="rightsConfirmed"/> must be true: the uploader says they may use it.</summary>
    Task<StoryReferenceDto> AddReferenceAsync(Guid tenantId, Guid? userId, Guid characterId, Stream content, string fileName, bool rightsConfirmed, CancellationToken ct = default);

    Task<StoryReferenceDto> ApproveReferenceAsync(Guid tenantId, Guid characterId, Guid referenceId, CancellationToken ct = default);

    Task<IReadOnlyList<StoryProjectDto>> ListProjectsAsync(Guid tenantId, CancellationToken ct = default);

    Task<StoryProjectDto> CreateProjectAsync(Guid tenantId, Guid? userId, CreateStoryProjectRequest request, CancellationToken ct = default);

    Task<StoryProjectDto> GetProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);

    Task<StoryProjectDto> SetVideoProviderAsync(Guid tenantId, Guid projectId, SceneVideoProviderKind provider, CancellationToken ct = default);

    Task<StoryScriptVersionDto> GenerateScriptAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default);

    Task<StoryScriptVersionDto> SaveScriptAsync(Guid tenantId, Guid projectId, SaveStoryScriptRequest request, CancellationToken ct = default);

    Task<StoryScriptVersionDto?> GetCurrentScriptAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);

    /// <summary>Approving freezes the version and creates its scenes. Nothing paid may start before this.</summary>
    Task<IReadOnlyList<StorySceneDto>> ApproveScriptAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default);

    Task<StoryCostEstimateDto> EstimateCostAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);
}

public static class StoryPrompts
{
    public const string Version = "cinematic-v1";

    public const string System =
        "You write short vertical-video scripts for an Indian audience. Reply with ONE JSON object and nothing else. " +
        "Shape: {title, description, hook, storySummary, language, punchline, characters:[{name, visualDescription, voiceInstructions}], " +
        "scenes:[{index, targetSeconds, setting, camera, transition, soundEffects, continuityNotes, videoPrompt, " +
        "dialogue:[{speaker, text, expression, subtitleText, startMs, endMs}]}]}. " +
        "Rules: scenes are numbered from 1; each scene is at most 8 seconds; dialogue times are milliseconds inside the scene, never overlap, and " +
        "every speaker is one of the declared characters. Write dialogue in the requested language (Hindi in Devanagari, or Hinglish in Latin letters). " +
        "videoPrompt describes only what the camera sees and hears, repeats each character's look for continuity, and keeps non-speakers' mouths still.";

    public static string User(StoryProjectDto project, IReadOnlyList<StoryCharacterDto> characters, int sceneCount, int sceneSeconds)
    {
        var cast = string.Join("\n", characters.Select(c => $"- {c.Name}: {c.VisualAttributes ?? c.Description}; personality {c.Personality}; voice {c.VoicePreferences}"));
        return $"Topic: {project.Topic}\nLanguage: {project.Language}\nStyle: {project.Style}\nAudience: {project.Audience}\nMoral or call to action: {project.Moral}\n" +
               $"Target length: {project.TargetSeconds} seconds as {sceneCount} scenes of about {sceneSeconds} seconds.\nCharacters:\n{cast}";
    }

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}

/// <summary>A deterministic script for the simulator and for tests. Marked as test data everywhere it is shown; it is never passed off as AI output.</summary>
public static class SimulatedScriptWriter
{
    public static CinematicScript Write(StoryProjectDto project, IReadOnlyList<StoryCharacterDto> characters, int sceneCount, int sceneSeconds)
    {
        var cast = characters.Count > 0
            ? characters.Select(c => new CinematicCharacter(c.Name, c.VisualAttributes ?? c.Description ?? c.Name, c.VoicePreferences ?? "natural")).ToList()
            : new List<CinematicCharacter> { new("Narrator", "A friendly storyteller", "calm") };

        var scenes = new List<CinematicScene>();
        for (var i = 1; i <= sceneCount; i++)
        {
            var speaker = cast[(i - 1) % cast.Count].Name;
            var ms = sceneSeconds * 1000;
            scenes.Add(new CinematicScene(i, sceneSeconds, "Test setting", "Medium shot, static", i == sceneCount ? "End" : "Cut", "Room tone", "Same look as scene 1",
                $"[Test data] Scene {i} of '{project.Title}': {project.Topic}. Vertical 9:16. {speaker} speaks to camera.",
                new[] { new CinematicDialogue(speaker, $"[Test data] Line {i} about {project.Topic}", "neutral", $"Line {i}", 500, Math.Min(ms - 500, 4000)) }));
        }

        return new CinematicScript($"[Test data] {project.Title}", "Predefined test script. No AI provider was contacted.", "[Test data] Hook",
            "[Test data] Summary", project.Language, "[Test data] Punchline", cast, scenes);
    }
}
