using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>
/// Renders a published knowledge version as the markdown of the tenant's one system-managed article: one section per published item, headed by its
/// question, with a marker line carrying the item's id. The structure-aware chunker never crosses a section, so each item becomes its own chunk (or an
/// atomic group of chunks when it is long), and the marker is how a chunk is tied back to its item.
/// </summary>
public static class ServingArticleRenderer
{
    private const int MaxHeadingChars = 200;

    public static string Render(IEnumerable<KnowledgeItem> items)
    {
        var text = new StringBuilder();
        var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Question, StringComparer.OrdinalIgnoreCase))
        {
            var heading = Heading(item.Question);
            // Two items with the same question must not share a heading: the heading is the section's identity, and equal ones could be merged into one chunk.
            for (var n = 2; !headings.Add(heading); n++)
                heading = $"{Heading(item.Question)} ({n})";

            text.Append("## ").Append(heading).Append('\n');
            text.Append(ProductKnowledgeMarkers.Marker(item.Id)).Append('\n');
            text.Append("Topic: ").Append(OneLine(item.Category)).Append("\n\n");
            text.Append(Safe(item.Summary)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(item.Detail))
                text.Append(Safe(item.Detail!)).Append("\n\n");
        }

        return text.ToString().TrimEnd() + "\n";
    }

    public static string HashOf(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Heading(string question)
    {
        var line = OneLine(question).TrimStart('#', ' ');
        if (line.Length == 0)
            line = "Question";
        return line.Length <= MaxHeadingChars ? line : line[..MaxHeadingChars];
    }

    private static string OneLine(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    /// <summary>Answer text is the tenant's own words. It must not be able to open a new section (a line starting with "#") or plant a marker that ties
    /// its chunk to some other item.</summary>
    private static string Safe(string value)
    {
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => Regex.IsMatch(l, @"^\s*#{1,6}\s") ? "\\" + l.TrimStart() : l);
        return ProductKnowledgeMarkers.Neutralize(string.Join("\n", lines)).Trim();
    }
}

/// <summary>The item-id marker line in the serving article, and how ingestion reads it back off the chunks (and removes it, so neither the embedding nor the
/// model ever sees it).</summary>
public static class ProductKnowledgeMarkers
{
    private static readonly Regex Pattern = new(@"^[ \t]*\[\[pk:item=(?<id>[0-9a-fA-F-]{36})\]\][ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.Compiled);

    public static string Marker(Guid itemId) => $"[[pk:item={itemId}]]";

    /// <summary>Breaks up any marker-looking text in tenant content.</summary>
    public static string Neutralize(string text) => text.Replace("[[pk:", "[ [pk:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the item ids off the chunks and strips the marker lines. A chunk with no marker of its own (the later parts of a long item) takes the item of
    /// the chunk before it when they share a section. Returns the cleaned drafts and, by chunk index, the item each belongs to.
    /// </summary>
    public static (IReadOnlyList<ChunkDraft> Drafts, IReadOnlyDictionary<int, Guid> ItemByIndex) Extract(IReadOnlyList<ChunkDraft> drafts)
    {
        var cleaned = new List<ChunkDraft>(drafts.Count);
        var links = new Dictionary<int, Guid>();
        ChunkDraft? previous = null;
        Guid? previousItem = null;

        foreach (var draft in drafts)
        {
            Guid? item = null;
            var match = Pattern.Match(draft.Body);
            if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var found))
                item = found;

            var body = Pattern.Replace(draft.Body, string.Empty).Trim();
            item ??= previous is not null && previous.ContextHeader == draft.ContextHeader ? previousItem : null;

            var clean = draft with { Body = body };
            cleaned.Add(clean);
            if (item is { } id)
                links[clean.Index] = id;

            previous = draft;
            previousItem = item;
        }

        return (cleaned, links);
    }
}
