namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

/// <summary>
/// Converts a semantic version string into a single sortable number.
///
/// This exists because <c>'2.10.0' &lt; '2.9.0'</c> is true under string comparison, and an article
/// whose applicability is bounded by version would then be filtered out - or worse, in - for exactly
/// the releases where the boundary mattered. It would be a silently wrong filter, which is the kind
/// this design goes out of its way to make impossible.
///
/// The numeric form is computed once during ingestion and stored on the chunk, so the retrieval
/// query compares integers rather than parsing strings per row.
/// </summary>
public static class SemanticVersion
{
    /// <summary>Multipliers chosen so each component has room for 999 before it would collide with
    /// the next - "2.9.0" and "2.10.0" must not only order correctly but stay distinct.</summary>
    private const long MajorMultiplier = 1_000_000;
    private const long MinorMultiplier = 1_000;

    /// <summary>The largest component value this encoding can represent without overflowing into the
    /// next place. A version past this is clamped rather than silently wrapping into a smaller
    /// number, which would order it BELOW versions it is newer than.</summary>
    public const int MaxComponent = 999;

    /// <summary>
    /// "2.10.3" becomes 2_010_003. Returns null for null, empty or unparseable input, which callers
    /// treat as "unbounded" - a malformed bound must widen applicability, never narrow it, because
    /// the alternative is an article silently disappearing over a typo.
    ///
    /// Tolerates a leading "v", a missing patch or minor ("2" and "2.1" are both valid), and trailing
    /// pre-release or build metadata ("2.1.0-rc.1"), which is ignored: a bound of "2.1.0-rc.1" and
    /// one of "2.1.0" describe the same release for applicability purposes.
    /// </summary>
    public static long? ToNumeric(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var text = version.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            text = text[1..];

        // Drop pre-release / build metadata.
        var cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
            text = text[..cut];

        var parts = text.Split('.');
        if (parts.Length is 0 or > 3)
            return null;

        var components = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value) || value < 0)
                return null;

            components[i] = Math.Min(value, MaxComponent);
        }

        return components[0] * MajorMultiplier
             + components[1] * MinorMultiplier
             + components[2];
    }
}
