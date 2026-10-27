using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.Ai.Tasks;

/// <summary>Helpers for the structured (JSON) replies product-knowledge tasks ask for.</summary>
public static class AiJson
{
    /// <summary>
    /// Pulls the JSON object out of a model reply - models often wrap it in a code fence or add a sentence around it - and returns it only
    /// if it really parses. Anything else is a malformed reply; the caller never guesses at it.
    /// </summary>
    public static bool TryExtractObject(string? reply, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        var text = reply.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        var candidate = text[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            json = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Keeps provider error text safe to store: short, single-line, and with anything key-shaped removed.</summary>
public static class AiLogScrubber
{
    private static readonly Regex KeyShapes = new(
        @"(sk-[A-Za-z0-9_\-]{8,})|(AIza[0-9A-Za-z_\-]{10,})|(Bearer\s+[A-Za-z0-9._\-]{8,})|(x-api-key\s*[:=]\s*\S+)|(api[_-]?key\s*[:=]\s*\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? Scrub(string? message, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var single = Regex.Replace(message, @"\s+", " ").Trim();
        single = KeyShapes.Replace(single, "[removed]");
        return single.Length <= max ? single : single[..max];
    }
}

/// <summary>
/// The simulator's replies for product-knowledge tasks. They are fixed, labelled <c>simulated</c>, carry no evidence, and so can never be
/// mistaken for - or promoted to - a verified answer: the simulator does not read sources and says so in every reply.
/// </summary>
public static class SimulatedTaskEngine
{
    public const string Marker = "[SIMULATED]";

    /// <summary>The reply for a task under a scenario, or null for <see cref="AiSimulatorScenarios.ProviderError"/> (which is a failure, not a reply).</summary>
    public static string? Respond(string task, string scenario, bool expectJson)
    {
        if (string.Equals(scenario, AiSimulatorScenarios.ProviderError, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!expectJson)
            return $"{Marker} This is a predefined test reply for {task}. It was not produced by an AI provider and is not based on your documents.";

        if (string.Equals(scenario, AiSimulatorScenarios.Malformed, StringComparison.OrdinalIgnoreCase))
            return $"{Marker} sorry, here is the answer: {{ \"answer\": ";

        var note = $"{Marker} Predefined test data. No source was read and no AI provider was contacted.";

        // The AI Content Studio's tasks: a fixed set of ideas or scenes, plainly marked, citing nothing the tenant said.
        if (task == AiTaskKinds.ContentTopicIdeas)
            return JsonSerializer.Serialize(new
            {
                topics = new[]
                {
                    new { title = $"{Marker} Meet the team behind the service", rationale = "Predefined test idea. It was not written from your facts.", facts = Array.Empty<int>() },
                    new { title = $"{Marker} Three questions customers ask before they book", rationale = "Predefined test idea. It was not written from your facts.", facts = Array.Empty<int>() },
                    new { title = $"{Marker} A day at the clinic", rationale = "Predefined test idea. It was not written from your facts.", facts = Array.Empty<int>() },
                },
                note
            });

        if (task == AiTaskKinds.ContentScript)
            return JsonSerializer.Serialize(new
            {
                scenes = new[]
                {
                    new { narration = $"{Marker} Welcome. This is a predefined test script.", onScreenText = "Welcome", durationSeconds = 6, visualHint = "Exterior of the business", facts = Array.Empty<int>() },
                    new { narration = $"{Marker} It says nothing about your business.", onScreenText = "Test script", durationSeconds = 8, visualHint = "Team at work", facts = Array.Empty<int>() },
                    new { narration = $"{Marker} Get in touch to find out more.", onScreenText = "Get in touch", durationSeconds = 6, visualHint = "Contact details", facts = Array.Empty<int>() },
                },
                note
            });

        if (string.Equals(scenario, AiSimulatorScenarios.MissingEvidence, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new
            {
                simulated = true, task, answer = (string?)null, evidence = Array.Empty<object>(), insufficientEvidence = true, conflicts = Array.Empty<object>(), note
            });

        if (string.Equals(scenario, AiSimulatorScenarios.Conflict, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new
            {
                simulated = true, task, answer = $"{Marker} The price differs between two sources.", evidence = Array.Empty<object>(), insufficientEvidence = false,
                conflicts = new[]
                {
                    new
                    {
                        fact = "price",
                        values = new[] { new { value = "999", source = "simulated brochure" }, new { value = "1499", source = "simulated website" } }
                    }
                },
                note
            });

        return JsonSerializer.Serialize(new
        {
            simulated = true, task, answer = $"{Marker} A predefined answer used for testing.", evidence = Array.Empty<object>(), insufficientEvidence = false,
            conflicts = Array.Empty<object>(), note
        });
    }
}
