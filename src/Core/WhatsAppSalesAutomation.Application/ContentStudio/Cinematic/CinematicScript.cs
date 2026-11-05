using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>The contract between the AI script service and the backend. The model must return exactly this JSON; anything else is rejected
/// by <see cref="CinematicScriptValidator"/> before a word is saved.</summary>
public sealed record CinematicScript(
    string Title,
    string Description,
    string Hook,
    string StorySummary,
    string Language,
    string Punchline,
    IReadOnlyList<CinematicCharacter> Characters,
    IReadOnlyList<CinematicScene> Scenes);

public sealed record CinematicCharacter(string Name, string VisualDescription, string VoiceInstructions);

public sealed record CinematicScene(
    int Index,
    int TargetSeconds,
    string Setting,
    string Camera,
    string Transition,
    string SoundEffects,
    string ContinuityNotes,
    /// <summary>The prompt for the video model (Veo, or pasted into Flow). Must not contain the dialogue as a substitute for audio.</summary>
    string VideoPrompt,
    IReadOnlyList<CinematicDialogue> Dialogue);

public sealed record CinematicDialogue(
    string Speaker,
    string Text,
    string Expression,
    string SubtitleText,
    int StartMs,
    int EndMs);

public sealed record ScriptValidationResult(bool IsValid, IReadOnlyList<string> Errors, CinematicScript? Script);

public static class CinematicScriptValidator
{
    public const int MaxScenes = 4;
    public const int MaxSceneSeconds = 10;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses and checks the model's reply. Speakers must be declared characters, dialogue must fit inside its scene and must not overlap
    /// (an overlap is allowed only between different speakers if the script says so, which this version does not: it is rejected).</summary>
    public static ScriptValidationResult Validate(string? json, int targetSeconds)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return new(false, new[] { "The script is empty." }, null);

        CinematicScript? script;
        try { script = JsonSerializer.Deserialize<CinematicScript>(json, Json); }
        catch (JsonException ex) { return new(false, new[] { $"The script is not valid JSON for the expected shape: {ex.Message}" }, null); }
        if (script is null) return new(false, new[] { "The script is empty." }, null);

        Need(script.Title, "title", errors);
        Need(script.Hook, "hook", errors);
        Need(script.StorySummary, "story summary", errors);
        Need(script.Punchline, "punchline", errors);

        var characters = script.Characters ?? Array.Empty<CinematicCharacter>();
        if (characters.Count == 0) errors.Add("The script declares no characters.");
        var names = new HashSet<string>(characters.Select(c => (c.Name ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);

        var scenes = script.Scenes ?? Array.Empty<CinematicScene>();
        if (scenes.Count == 0 || scenes.Count > MaxScenes) errors.Add($"The script needs 1 to {MaxScenes} scenes, not {scenes.Count}.");

        var total = 0;
        for (var i = 0; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var label = $"Scene {i + 1}";
            if (scene.Index != i + 1) errors.Add($"{label}: index must be {i + 1}, was {scene.Index}.");
            if (scene.TargetSeconds is < 1 or > MaxSceneSeconds) errors.Add($"{label}: duration must be 1 to {MaxSceneSeconds} seconds.");
            total += scene.TargetSeconds;
            Need(scene.VideoPrompt, $"{label} video prompt", errors);

            var lastEnd = 0;
            foreach (var line in (scene.Dialogue ?? Array.Empty<CinematicDialogue>()).OrderBy(d => d.StartMs))
            {
                if (!names.Contains((line.Speaker ?? string.Empty).Trim())) errors.Add($"{label}: '{line.Speaker}' is not a declared character.");
                if (string.IsNullOrWhiteSpace(line.Text)) errors.Add($"{label}: a dialogue line from {line.Speaker} is empty.");
                if (line.StartMs < 0 || line.EndMs <= line.StartMs) errors.Add($"{label}: a line from {line.Speaker} has an invalid time range.");
                if (line.EndMs > scene.TargetSeconds * 1000) errors.Add($"{label}: a line from {line.Speaker} runs past the end of the scene.");
                if (line.StartMs < lastEnd) errors.Add($"{label}: dialogue overlaps at {line.StartMs} ms.");
                lastEnd = Math.Max(lastEnd, line.EndMs);
            }
        }

        // Providers don't return exact lengths, so allow slack; a script far off target is a planning error, not a trim.
        if (scenes.Count > 0 && Math.Abs(total - targetSeconds) > Math.Max(4, targetSeconds / 5))
            errors.Add($"Scenes add up to {total} seconds; the target is {targetSeconds}.");

        return new(errors.Count == 0, errors, errors.Count == 0 ? script : null);
    }

    /// <summary>Hash of what an approval covers: words, speakers and timing. Camera notes can be tidied without invalidating an approval.</summary>
    public static string ContentHash(CinematicScript script)
    {
        var sb = new StringBuilder();
        foreach (var scene in script.Scenes)
        {
            sb.Append('#').Append(scene.Index).Append('|').Append(scene.TargetSeconds).Append('|').Append(scene.VideoPrompt.Trim());
            foreach (var d in scene.Dialogue ?? Array.Empty<CinematicDialogue>())
                sb.Append('\n').Append(d.Speaker.Trim()).Append('|').Append(d.Text.Trim()).Append('|').Append(d.StartMs).Append('|').Append(d.EndMs);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void Need(string? value, string what, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"The {what} is missing.");
    }
}
