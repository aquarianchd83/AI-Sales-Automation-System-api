using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Application.ContentStudio.Knowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Planner;

/// <summary>
/// The wording the planner gives the AI. Kept apart from the service so a change to a prompt is one visible edit with a new
/// <see cref="Version"/>, which is stored on every script it writes. Industry knowledge is never here: it comes from the tenant's
/// verified facts, which the prompt only quotes.
/// </summary>
/// <summary>
/// The choices a cinematic script makes for each scene. Closed lists, so whatever the AI writes is turned into something the video maker really
/// understands - or into the plain default - and never passed on as free text.
/// </summary>
public static class CinemaOptions
{
    public static readonly string[] Styles = { "Standard", "Cinematic" };
    public static readonly string[] CameraMoves = { "ZoomIn", "ZoomOut", "PanLeft", "PanRight", "Still" };
    public static readonly string[] Moods = { "Natural", "Warm", "Cool", "Dramatic", "Bright", "Dark" };
    public static readonly string[] Transitions = { "None", "Fade", "Wipe" };

    public const string DefaultStyle = "Standard";
    public const string DefaultCamera = "ZoomIn";
    public const string DefaultMood = "Natural";
    public const string DefaultTransition = "None";

    /// <summary>The canonical spelling of a choice ("zoom in", "Zoom-In" and "zoomin" all mean ZoomIn), or the default when it is not one of the list.</summary>
    public static string Pick(string? value, string[] allowed, string fallback)
    {
        var key = (value ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).Trim();
        return allowed.FirstOrDefault(a => a.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    public static string? Shot(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= 40 ? trimmed : trimmed[..40];
    }
}

public static class PlannerPrompts
{
    public const string Version = "planner-v2";

    private const string Grounding =
        "You write only from the numbered verified facts you are given. Never state a price, figure, percentage, result, guarantee, qualification or comparison that is not in a fact. " +
        "Never invent names, places, awards or testimonials. A greeting, a question or an invitation to get in touch needs no fact. Reply with JSON only.";

    public static string TopicsSystem(string language) =>
        $"You plan short marketing videos for a business. {Grounding} Write in {language}. " +
        "Reply as {\"topics\":[{\"title\":\"...\",\"rationale\":\"why it suits the goal\",\"facts\":[1,2]}]}. " +
        "Every topic must rest on at least one fact, named by its number in \"facts\".";

    public static string TopicsUser(string goal, int count, IReadOnlyCollection<string> existing, string factsBlock)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal of the content: {goal}");
        sb.AppendLine($"Suggest {count} different video topics.");
        if (existing.Count > 0)
            sb.AppendLine("Do not repeat these ideas that were already suggested: " + string.Join("; ", existing));
        sb.AppendLine();
        sb.Append(factsBlock);
        return sb.ToString();
    }

    public static string ScriptSystem(string language, int targetSeconds, int? sceneCount = null, bool cinematic = false)
    {
        string shape;
        if (sceneCount is { } exact)
        {
            var each = Math.Max(2, (int)Math.Round(targetSeconds / (double)exact));
            shape = $"in exactly {exact} scene{(exact == 1 ? string.Empty : "s")} of about {each} seconds each; each scene is a separate short clip that must make sense on its own";
        }
        else
        {
            var most = Math.Clamp(targetSeconds / 4, 1, 10);
            var fewest = Math.Clamp(targetSeconds / 20, 1, most);
            shape = $"in {fewest} to {most} scenes of 4 to 20 seconds each";
        }

        var text =
            $"You write the script of a short marketing video, scene by scene. {Grounding} Write the narration in {language}; keep it spoken, simple and warm. " +
            $"The whole video should run about {targetSeconds} seconds, {shape}. ";

        if (cinematic)
            return text +
                "Write it as a cinematic short: open on a strong visual hook, make every scene one clear shot, and keep one consistent mood across the scenes. " +
                "For each scene also choose: \"shotType\" (wide, medium, close-up, detail or aerial), \"cameraMove\" (ZoomIn, ZoomOut, PanLeft, PanRight or Still), " +
                "\"mood\" (Natural, Warm, Cool, Dramatic, Bright or Dark) and \"transition\" (None, Fade or Wipe). The pictures are still photographs the business supplies, " +
                "so never describe real footage, actors or places the business has not provided. " +
                "Reply as {\"scenes\":[{\"narration\":\"what is said\",\"onScreenText\":\"a few words shown on screen\",\"durationSeconds\":8,\"visualHint\":\"what the picture shows\"," +
                "\"shotType\":\"wide\",\"cameraMove\":\"ZoomIn\",\"mood\":\"Warm\",\"transition\":\"Fade\",\"facts\":[1]}]}. " +
                "Name in \"facts\" the numbers of the facts a scene rests on.";

        return text +
            "Reply as {\"scenes\":[{\"narration\":\"what is said\",\"onScreenText\":\"a few words shown on screen\",\"durationSeconds\":8,\"visualHint\":\"what the picture shows\",\"facts\":[1]}]}. " +
            "Name in \"facts\" the numbers of the facts a scene rests on.";
    }

    public static string ScriptUser(string goal, string topic, string? rationale, string? notes, string factsBlock)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal of the content: {goal}");
        sb.AppendLine($"Video topic: {topic}");
        if (!string.IsNullOrWhiteSpace(rationale))
            sb.AppendLine($"Why this topic: {rationale}");
        if (!string.IsNullOrWhiteSpace(notes))
            sb.AppendLine($"Also keep in mind: {notes}");
        sb.AppendLine();
        sb.Append(factsBlock);
        return sb.ToString();
    }
}

public sealed record ParsedTopic(string Title, string? Rationale, IReadOnlyList<int> FactNumbers);

public sealed record ParsedScene(
    string Narration, string? OnScreenText, int DurationSeconds, string? VisualHint, IReadOnlyList<int> FactNumbers,
    string? ShotType = null, string CameraMove = CinemaOptions.DefaultCamera, string Mood = CinemaOptions.DefaultMood, string Transition = CinemaOptions.DefaultTransition);

/// <summary>
/// Reads the AI's JSON reply and keeps only what is usable: bounded text, durations in a sensible range, fact numbers that really exist.
/// A reply that is not the expected shape yields nothing rather than a guess.
/// </summary>
public static class PlannerReply
{
    public const int MaxScenes = 20;
    public const int MinSceneSeconds = 2;
    public const int MaxSceneSeconds = 30;

    public static IReadOnlyList<ParsedTopic> ParseTopics(string? json, int factCount, int max, bool requireFacts)
    {
        var topics = new List<ParsedTopic>();
        if (!TryArray(json, "topics", out var array))
            return topics;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || topics.Count >= max)
                continue;
            var title = Text(item, "title", 250);
            if (string.IsNullOrEmpty(title))
                continue;
            var facts = Facts(item, factCount);
            if (requireFacts && facts.Count == 0)
                continue; // an idea that rests on no fact of the tenant's is a guess
            topics.Add(new ParsedTopic(title, Text(item, "rationale", 1000), facts));
        }

        return topics;
    }

    public static IReadOnlyList<ParsedScene> ParseScenes(string? json, int factCount)
    {
        var scenes = new List<ParsedScene>();
        if (!TryArray(json, "scenes", out var array))
            return scenes;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || scenes.Count >= MaxScenes)
                continue;
            var narration = Text(item, "narration", 2000);
            if (string.IsNullOrEmpty(narration))
                continue;
            var seconds = item.TryGetProperty("durationSeconds", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) ? n : 8;
            scenes.Add(new ParsedScene(
                narration, Text(item, "onScreenText", 300), Math.Clamp(seconds, MinSceneSeconds, MaxSceneSeconds), Text(item, "visualHint", 500), Facts(item, factCount),
                CinemaOptions.Shot(Text(item, "shotType", 40)), CinemaOptions.Pick(Text(item, "cameraMove", 20), CinemaOptions.CameraMoves, CinemaOptions.DefaultCamera),
                CinemaOptions.Pick(Text(item, "mood", 20), CinemaOptions.Moods, CinemaOptions.DefaultMood), CinemaOptions.Pick(Text(item, "transition", 20), CinemaOptions.Transitions, CinemaOptions.DefaultTransition)));
        }

        return scenes;
    }

    private static bool TryArray(string? json, string name, out JsonElement array)
    {
        array = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(name, out var found) || found.ValueKind != JsonValueKind.Array)
                return false;
            array = found.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement item, string name, int max)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= max ? text : text[..max];
    }

    private static List<int> Facts(JsonElement item, int factCount)
    {
        var numbers = new List<int>();
        if (item.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            foreach (var f in facts.EnumerateArray())
                if (f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var n) && n >= 1 && n <= factCount && !numbers.Contains(n))
                    numbers.Add(n);
        return numbers;
    }
}

public sealed record SceneClaimOutcome(ClaimStatus Status, string Note, IReadOnlyList<Guid> SupportingFactIds);

/// <summary>
/// Decides, in code, whether a scene's narration is backed by what the tenant has verified. Each sentence that states something is checked
/// against the facts (see <see cref="KnowledgeClaimChecker"/>); a greeting, a question or an invitation states nothing and needs no fact.
/// The AI's own list of facts is a pointer for the reader, never the proof.
/// </summary>
public static class PlannerClaims
{
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?।])\s+", RegexOptions.Compiled);
    private static readonly Regex Marker = new(@"\[SIMULATED\]\s*", RegexOptions.Compiled);

    private static readonly Regex Invitation = new(
        @"^\s*(book|call|visit|contact|click|subscribe|follow|message|whatsapp|learn|watch|ask|get in touch|reach out|write to|drop|come|join|register|sign up|schedule|take|try|see|find out|discover|share|tap|send|dm|like|comment|welcome|hello|hi|hey|namaste|thank)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> Sentences(string narration) =>
        SentenceBreak.Split(Marker.Replace(narration ?? string.Empty, string.Empty)).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    public static bool StatesSomething(string sentence) =>
        !sentence.EndsWith('?') && !Invitation.IsMatch(sentence) && sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;

    public static SceneClaimOutcome Evaluate(string narration, IReadOnlyList<BusinessFact> facts, DateTime utcNow)
    {
        var claims = Sentences(narration).Where(StatesSomething).ToList();
        if (claims.Count == 0)
            return new SceneClaimOutcome(ClaimStatus.NoClaim, "States nothing that needs a fact behind it.", Array.Empty<Guid>());

        var result = KnowledgeClaimChecker.Check(claims, facts, utcNow);
        var supporting = result.Claims.SelectMany(c => c.SupportingFactIds).Distinct().ToList();

        var worst = result.Claims.FirstOrDefault(c => c.Verdict == ClaimVerdict.Unsupported) ?? result.Claims.FirstOrDefault(c => c.Verdict == ClaimVerdict.Unverified);
        if (worst is null)
            return new SceneClaimOutcome(ClaimStatus.Supported, supporting.Count == 1 ? "Backed by 1 verified fact." : $"Backed by {supporting.Count} verified facts.", supporting);

        var status = worst.Verdict == ClaimVerdict.Unsupported ? ClaimStatus.Unsupported : ClaimStatus.Unverified;
        return new SceneClaimOutcome(status, Trim($"\"{worst.Claim}\" - {worst.Reason}", 600), supporting);
    }

    /// <summary>A fingerprint of what the script says, so a later change to it can be told from no change.</summary>
    public static string Hash(IEnumerable<(string Narration, string? OnScreen)> scenes)
    {
        var text = string.Join('\n', scenes.Select(s => s.Narration + "\u001f" + s.OnScreen));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
