using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

/// <summary>One passage before it is stored: its text and the heading it sits under.</summary>
public sealed record PassageDraft(string Text, string? Heading);

/// <summary>
/// Cuts a page of text into passages - the pieces later answers cite. A passage is a few paragraphs under one heading, small enough to quote
/// and check, big enough to carry a whole fact. Paragraphs are never split mid-sentence unless one alone is too long, and a list or table
/// stays with the paragraph that introduces it. The heading is kept with each passage so a reference can say "Pricing &gt; Plans".
/// </summary>
public static class PassageSplitter
{
    public const int TargetChars = 1000;
    public const int MaxChars = 1600;
    public const int MinChars = 40;

    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    public static IReadOnlyList<PassageDraft> Split(string text)
    {
        var passages = new List<PassageDraft>();
        if (string.IsNullOrWhiteSpace(text))
            return passages;

        var headings = new string?[6];
        var buffer = new StringBuilder();

        string? CurrentHeading()
        {
            var parts = headings.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            if (parts.Count == 0)
                return null;
            var joined = string.Join(" > ", parts);
            return joined.Length > 300 ? joined[..300] : joined;
        }

        void Flush()
        {
            var body = buffer.ToString().Trim();
            buffer.Clear();
            if (body.Length == 0)
                return;
            foreach (var piece in Cut(body))
                passages.Add(new PassageDraft(piece, CurrentHeading()));
        }

        foreach (var block in Blocks(text))
        {
            var heading = Heading.Match(block);
            if (heading.Success && !block.Contains('\n'))
            {
                Flush();
                var level = heading.Groups[1].Value.Length;
                headings[level - 1] = heading.Groups[2].Value.Trim();
                for (var i = level; i < headings.Length; i++)
                    headings[i] = null;
                continue;
            }

            if (buffer.Length > 0 && buffer.Length + block.Length + 2 > TargetChars)
                Flush();

            if (buffer.Length > 0)
                buffer.Append("\n\n");
            buffer.Append(block);
        }

        Flush();

        // A tiny leftover ("Call us today.") joins the passage before it rather than standing alone as weak evidence.
        for (var i = passages.Count - 1; i > 0; i--)
        {
            if (passages[i].Text.Length < MinChars && passages[i].Heading == passages[i - 1].Heading
                && passages[i - 1].Text.Length + passages[i].Text.Length + 2 <= MaxChars)
            {
                passages[i - 1] = passages[i - 1] with { Text = passages[i - 1].Text + "\n\n" + passages[i].Text };
                passages.RemoveAt(i);
            }
        }

        return passages;
    }

    /// <summary>Lowercased, with runs of whitespace collapsed: two passages that differ only in spacing are the same passage.</summary>
    public static string Normalise(string text) => Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(text)))).ToLowerInvariant();

    /// <summary>Paragraph blocks: separated by blank lines; list items and table rows stay together with their block.</summary>
    private static IEnumerable<string> Blocks(string text) =>
        Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n").Select(b => b.Trim('\n', ' ', '\t')).Where(b => b.Trim().Length > 0);

    /// <summary>A block too long to be one passage is cut at sentence ends, then - for a sentence longer than a passage - at word boundaries.</summary>
    private static IEnumerable<string> Cut(string body)
    {
        if (body.Length <= MaxChars)
        {
            yield return body;
            yield break;
        }

        var sentences = Regex.Split(body, @"(?<=[.!?।])\s+");
        var piece = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (piece.Length > 0 && piece.Length + sentence.Length + 1 > TargetChars)
            {
                yield return piece.ToString().Trim();
                piece.Clear();
            }

            if (sentence.Length > MaxChars)
            {
                foreach (var part in ByWords(sentence))
                    yield return part;
                continue;
            }

            if (piece.Length > 0)
                piece.Append(' ');
            piece.Append(sentence);
        }

        if (piece.Length > 0)
            yield return piece.ToString().Trim();
    }

    private static IEnumerable<string> ByWords(string sentence)
    {
        var piece = new StringBuilder();
        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece.Length + word.Length + 1 > TargetChars && piece.Length > 0)
            {
                yield return piece.ToString();
                piece.Clear();
            }
            if (piece.Length > 0)
                piece.Append(' ');
            piece.Append(word.Length > MaxChars ? word[..MaxChars] : word);
        }
        if (piece.Length > 0)
            yield return piece.ToString();
    }
}
