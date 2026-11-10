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
    Guid Id, int Number, StoryScriptStatus Status, string Source, string ContentHash, CinematicScript Script, bool IsSimulated, DateTime CreatedAt,
    IReadOnlyList<StoryKnowledgeFactDto>? FactsUsed = null, IReadOnlyList<ScriptClaimDto>? Claims = null);

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

    /// <param name="chargeTenant">False when the work is already paid for (a requested video's price covers its script).</param>
    Task<StoryScriptVersionDto> GenerateScriptAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default, bool chargeTenant = true);

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

    public const string IdeasSystem =
        "You suggest short vertical marketing videos for a small business. Each idea is ONE real problem a customer has that this business solves. " +
        "Use only the verified facts you are given; never invent a product, price, result or promise. Reply with ONE JSON object and nothing else: " +
        "{ideas:[{title, problem, solution, factIds:[...]}]}. title is short and catchy; problem is what the customer struggles with, in one sentence; " +
        "solution is how this business helps, in one sentence, using only the facts; factIds are ids copied exactly from the facts list that the idea rests on (at least one). " +
        "Facts in the Problems category are the problems the business says its product solves: build the ideas on those first, one idea per problem.";

    public static string IdeasUser(IReadOnlyList<WhatsAppSalesAutomation.Application.ContentStudio.Knowledge.GroundingFactDto> facts, string? language, int count) =>
        $"Suggest {count} different video ideas, written in {(string.IsNullOrWhiteSpace(language) ? "English" : language)}.\n\nVerified facts (id in brackets):\n{StoryIdeaPrompts.FactLines(facts)}";

    public static string User(StoryProjectDto project, IReadOnlyList<StoryCharacterDto> characters, int sceneCount, int sceneSeconds, string? groundingBlock = null)
    {
        var cast = characters.Count == 0
            ? "No characters were chosen. Use at most two simple speakers (for example a Customer and the business Owner), or one Narrator, and declare them in characters."
            : string.Join("\n", characters.Select(c => $"- {c.Name}: {c.VisualAttributes ?? c.Description}; personality {c.Personality}; voice {c.VoicePreferences}"));
        var grounded = groundingBlock is null ? string.Empty :
            groundingBlock + "\n\nStructure: scene 1 shows the customer's problem and hooks the viewer in the first two seconds; the middle scene shows how this business solves it, using only the facts above; " +
            "the last scene is a clear, simple call to action. Do not state any price, number, percentage, result or guarantee that is not in the facts.\n\n";
        return grounded + $"Topic: {project.Topic}\nLanguage: {project.Language}\nStyle: {project.Style}\nAudience: {project.Audience}\nMoral or call to action: {project.Moral}\n" +
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
