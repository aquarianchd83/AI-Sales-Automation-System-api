using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

/// <summary>Thrown for a document that must be rejected rather than indexed - wrong type, too big,
/// too short, unreadable. A content decision, so retrying it unchanged is pointless.</summary>
public sealed class DocumentRejectedException : Exception
{
    public DocumentRejectedException(string reasonCode, string message) : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <param name="QualityScore">0..1 confidence that reading order and structure survived extraction.
/// Always 1.0 for formats that carry their own structure. Below <see cref="ExtractionResult.ReviewThreshold"/>
/// the article must not be auto-approved (G.3).</param>
public sealed record ExtractionResult(string Markdown, string Format, double QualityScore, IReadOnlyList<string> Warnings)
{
    public const double ReviewThreshold = 0.7;

    public bool NeedsHumanReview => QualityScore < ReviewThreshold;
}

/// <summary>Reads one file format into the markdown the chunker understands.</summary>
public interface IDocumentFormatExtractor
{
    /// <summary>Lowercase extensions including the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    ExtractionResult Extract(Stream content);
}

/// <summary>Picks the extractor by extension and enforces the upload limits (G.2).</summary>
public sealed class DocumentTextExtractor
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MinContentChars = 120;
    public const int MaxContentChars = 200_000;

    private readonly IReadOnlyList<IDocumentFormatExtractor> _extractors;

    public DocumentTextExtractor(IEnumerable<IDocumentFormatExtractor> extractors)
    {
        _extractors = extractors.ToList();
    }

    public IReadOnlyList<string> SupportedExtensions => _extractors.SelectMany(e => e.Extensions).Distinct().ToList();

    public ExtractionResult Extract(string fileName, long length, Stream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var extractor = _extractors.FirstOrDefault(e => e.Extensions.Contains(extension))
            ?? throw new DocumentRejectedException("UnsupportedType",
                $"'{extension}' is not supported. Supported: {string.Join(", ", SupportedExtensions)}.");

        if (length > MaxFileBytes)
            throw new DocumentRejectedException("TooLarge", $"File is {length / 1024 / 1024} MB; the limit is {MaxFileBytes / 1024 / 1024} MB.");

        var result = extractor.Extract(content);

        var chars = result.Markdown.Trim().Length;
        if (chars < MinContentChars)
            throw new DocumentRejectedException("TooShort", $"Extracted {chars} characters; at least {MinContentChars} are required. A scanned document has no text layer to read.");

        if (chars > MaxContentChars)
            throw new DocumentRejectedException("TooLong", $"Extracted {chars} characters; the limit is {MaxContentChars}. Split it into several articles.");

        return result;
    }
}

/// <summary>.md passes straight through; .txt has headings inferred, since the chunker cannot see
/// structure that is not marked.</summary>
public sealed class TextDocumentExtractor : IDocumentFormatExtractor
{
    public IReadOnlyList<string> Extensions { get; } = new[] { ".md", ".markdown", ".txt" };

    public ExtractionResult Extract(Stream content)
    {
        string text;
        try
        {
            // throwOnInvalidBytes: an upload that is not UTF-8 is rejected, not silently mangled.
            using var reader = new StreamReader(content, new UTF8Encoding(false, throwOnInvalidBytes: true));
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            throw new DocumentRejectedException("BadEncoding", "The file is not valid UTF-8.");
        }

        return new ExtractionResult(InferHeadings(text).Trim(), "text", 1.0, Array.Empty<string>());
    }

    /// <summary>
    /// ALL-CAPS lines and short lines ending in a colon, standing alone between blank lines, become
    /// headings. Markdown input is left alone if it already has any heading.
    ///
    /// Numbered lines are deliberately NOT treated as headings: "1. Open Billing" is a step of a
    /// procedure, which the chunker must keep whole, and reading it as a heading would break that.
    /// </summary>
    public static string InferHeadings(string text)
    {
        if (Regex.IsMatch(text, @"^#{1,6}\s", RegexOptions.Multiline))
            return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var standsAlone = (i == 0 || lines[i - 1].Trim().Length == 0)
                              && (i == lines.Length - 1 || lines[i + 1].Trim().Length == 0);

            var isCaps = line.Length is >= 3 and <= 80
                         && line.Any(char.IsLetter)
                         && !line.Any(char.IsLower);
            var isLabel = line.Length is >= 3 and <= 60 && line.EndsWith(':') && !line.Contains(". ");

            output.AppendLine(standsAlone && (isCaps || isLabel)
                ? "## " + line.TrimEnd(':')
                : lines[i]);
        }

        return output.ToString();
    }
}

/// <summary>HTML to markdown, without a DOM. Enough for authored help content: headings, paragraphs,
/// lists and tables. Scripts, styles and comments are removed entirely, contents included - they are
/// where an injected instruction hides in a page a reviewer only sees rendered.</summary>
public sealed class HtmlDocumentExtractor : IDocumentFormatExtractor
{
    public IReadOnlyList<string> Extensions { get; } = new[] { ".html", ".htm" };

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    public ExtractionResult Extract(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        return new ExtractionResult(ToMarkdown(reader.ReadToEnd()), "html", 1.0, Array.Empty<string>());
    }

    public static string ToMarkdown(string html)
    {
        var text = Regex.Replace(html, @"<!--.*?-->", string.Empty, Opt);
        text = Regex.Replace(text, @"<(script|style|noscript|template)\b.*?</\1\s*>", string.Empty, Opt);

        // Elements hidden from a human reader but present for a model.
        text = Regex.Replace(text, @"<(\w+)\b[^>]*(display\s*:\s*none|visibility\s*:\s*hidden|\bhidden\b)[^>]*>.*?</\1\s*>", string.Empty, Opt);

        for (var level = 1; level <= 6; level++)
        {
            var hashes = new string('#', level);
            text = Regex.Replace(text, $@"<h{level}\b[^>]*>(.*?)</h{level}\s*>", m => $"\n\n{hashes} {Inline(m.Groups[1].Value)}\n\n", Opt);
        }

        // Ordered lists keep their numbers: a numbered procedure is an atomic unit downstream.
        text = Regex.Replace(text, @"<ol\b[^>]*>(.*?)</ol\s*>", m =>
        {
            var n = 0;
            return "\n\n" + Regex.Replace(m.Groups[1].Value, @"<li\b[^>]*>(.*?)</li\s*>", li => $"{++n}. {Inline(li.Groups[1].Value)}\n", Opt) + "\n";
        }, Opt);

        text = Regex.Replace(text, @"<li\b[^>]*>(.*?)</li\s*>", m => $"- {Inline(m.Groups[1].Value)}\n", Opt);

        text = Regex.Replace(text, @"<table\b[^>]*>(.*?)</table\s*>", m => "\n\n" + Table(m.Groups[1].Value) + "\n\n", Opt);

        text = Regex.Replace(text, @"<(br|hr)\b[^>]*/?>", "\n", Opt);
        text = Regex.Replace(text, @"</(p|div|section|article|ul|ol|blockquote)\s*>", "\n\n", Opt);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty, Opt);

        return WebUtility.HtmlDecode(text).Trim();
    }

    private static string Inline(string fragment) =>
        WebUtility.HtmlDecode(Regex.Replace(fragment, @"<[^>]+>", string.Empty)).Trim().Replace("\n", " ");

    private static string Table(string body)
    {
        var rows = Regex.Matches(body, @"<tr\b[^>]*>(.*?)</tr\s*>", Opt)
            .Select(r => Regex.Matches(r.Groups[1].Value, @"<t[hd]\b[^>]*>(.*?)</t[hd]\s*>", Opt)
                .Select(c => Inline(c.Groups[1].Value).Replace("|", "\\|")).ToList())
            .Where(r => r.Count > 0).ToList();

        if (rows.Count == 0)
            return string.Empty;

        var lines = new List<string> { "| " + string.Join(" | ", rows[0]) + " |", "|" + string.Concat(rows[0].Select(_ => "---|")) };
        lines.AddRange(rows.Skip(1).Select(r => "| " + string.Join(" | ", r) + " |"));
        return string.Join("\n", lines);
    }
}

/// <summary>.docx via the package's own XML, no Office library: heading styles become #/##, tables
/// become markdown tables, list paragraphs become list items.</summary>
public sealed class DocxDocumentExtractor : IDocumentFormatExtractor
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public IReadOnlyList<string> Extensions { get; } = new[] { ".docx" };

    public ExtractionResult Extract(Stream content)
    {
        XDocument document;
        try
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.GetEntry("word/document.xml")
                ?? throw new DocumentRejectedException("Corrupt", "Not a valid .docx: word/document.xml is missing.");

            // Guard against a zip bomb: the declared size is checked before anything is decompressed.
            if (entry.Length > DocumentTextExtractor.MaxFileBytes * 5)
                throw new DocumentRejectedException("TooLarge", "The document's content is implausibly large once unpacked.");

            using var stream = entry.Open();
            document = XDocument.Load(stream);
        }
        catch (InvalidDataException)
        {
            throw new DocumentRejectedException("Corrupt", "Not a valid .docx file.");
        }

        var body = document.Root?.Element(W + "body");
        var builder = new StringBuilder();

        foreach (var element in body?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (element.Name == W + "p")
                builder.Append(Paragraph(element)).Append("\n\n");
            else if (element.Name == W + "tbl")
                builder.Append(Table(element)).Append("\n\n");
        }

        return new ExtractionResult(builder.ToString().Trim(), "docx", 1.0, Array.Empty<string>());
    }

    private static string Paragraph(XElement p)
    {
        var text = ParagraphText(p).Trim();
        if (text.Length == 0)
            return string.Empty;

        var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? string.Empty;
        var match = Regex.Match(style, @"^Heading\s*([1-6])$", RegexOptions.IgnoreCase);

        if (match.Success)
            return new string('#', int.Parse(match.Groups[1].Value)) + " " + text;
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
            return "# " + text;
        if (p.Element(W + "pPr")?.Element(W + "numPr") is not null)
            return "- " + text;

        return text;
    }

    private static string ParagraphText(XElement p)
    {
        var builder = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            if (node.Name == W + "t")
                builder.Append(node.Value);
            else if (node.Name == W + "tab")
                builder.Append(' ');
            else if (node.Name == W + "br")
                builder.Append(' ');
        }

        return builder.ToString();
    }

    private static string Table(XElement table)
    {
        var rows = table.Elements(W + "tr")
            .Select(tr => tr.Elements(W + "tc").Select(tc => string.Join(" ", tc.Elements(W + "p").Select(ParagraphText)).Trim().Replace("|", "\\|")).ToList())
            .Where(r => r.Count > 0).ToList();

        if (rows.Count == 0)
            return string.Empty;

        var lines = new List<string> { "| " + string.Join(" | ", rows[0]) + " |", "|" + string.Concat(rows[0].Select(_ => "---|")) };
        lines.AddRange(rows.Skip(1).Select(r => "| " + string.Join(" | ", r) + " |"));
        return string.Join("\n", lines);
    }
}
