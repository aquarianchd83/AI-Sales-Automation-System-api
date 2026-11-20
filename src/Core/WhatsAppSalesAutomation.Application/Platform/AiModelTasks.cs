namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// The tasks a model can be recommended for (AI Provider Models > Best for, and the Compare screen). Keys are stored on the model, so never rename one;
/// the screen shows its own label for each. A new task is one more key here and one more label there.
/// </summary>
public static class AiModelTasks
{
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "customer-replies",
        "business-keywords",
        "description-rewrite",
        "discovery-keywords",
        "document-analysis",
        "information-extraction",
        "scanned-pages",
        "content-studio",
        "discover-customers"
    };

    public static bool IsKnown(string? key) => key != null && Keys.Contains(key.Trim().ToLowerInvariant());

    /// <summary>Known keys only, trimmed, lower-cased, no duplicates, in catalogue order - the stored form.</summary>
    public static string Join(IEnumerable<string>? tasks)
    {
        var wanted = new HashSet<string>((tasks ?? Array.Empty<string>()).Select(t => t.Trim().ToLowerInvariant()));
        return string.Join(',', Keys.Where(wanted.Contains));
    }

    public static string[] Split(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? Array.Empty<string>() : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
