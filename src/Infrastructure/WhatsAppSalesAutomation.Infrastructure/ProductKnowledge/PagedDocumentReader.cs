using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

namespace WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;

/// <summary>
/// Reads a tenant's document page by page so every passage can say which page it came from. PDF with PdfPig (free, already in the
/// project): the text layer of each page; a page with no text layer but a picture is a scanned page and its picture is kept, to be read with
/// the AI provider only if the platform allows it. Photos of documents (.png, .jpg) are one scanned page. Word, text, markdown and HTML go
/// through the knowledge base's existing extractors, as one page without a number.
///
/// Checks that the bytes really are what the name says (a ".pdf" that is not a PDF is refused) before any parser sees them.
/// </summary>
public sealed class PagedDocumentReader : IPagedDocumentReader
{
    /// <summary>A page with fewer letters than this in its text layer is treated as having none (a scanned page often carries a stray page number).</summary>
    private const int MinPageTextChars = 25;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    private readonly IReadOnlyList<IDocumentFormatExtractor> _otherFormats;

    public PagedDocumentReader(IEnumerable<IDocumentFormatExtractor> extractors)
    {
        // PDF is read here, page by page; the knowledge base's PDF extractor joins pages and drops the numbers.
        _otherFormats = extractors.Where(e => !e.Extensions.Contains(".pdf")).ToList();
    }

    public IReadOnlyList<string> SupportedExtensions =>
        new[] { ".pdf" }.Concat(ImageExtensions).Concat(_otherFormats.SelectMany(e => e.Extensions)).Distinct().ToList();

    public ReadDocument Read(string fileName, Stream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();

        EnsureAcceptable(fileName, bytes);

        if (extension == ".pdf")
            return ReadPdf(bytes);

        if (ImageExtensions.Contains(extension))
            return new ReadDocument("image", new[] { new DocumentPage(1, string.Empty, bytes, extension == ".png" ? "image/png" : "image/jpeg") }, 1.0, Array.Empty<string>());

        var extractor = _otherFormats.First(e => e.Extensions.Contains(extension));
        var result = extractor.Extract(new MemoryStream(bytes));
        return new ReadDocument(result.Format, new[] { new DocumentPage(null, result.Markdown) }, result.QualityScore, result.Warnings);
    }

    public void EnsureAcceptable(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            throw new DocumentRejectedException("UnsupportedType", $"'{(extension.Length == 0 ? "(no extension)" : extension)}' files are not supported. Use {string.Join(", ", SupportedExtensions)}.");
        if (bytes.Length == 0)
            throw new DocumentRejectedException("Empty", "The file is empty.");
        if (!LooksLike(extension, bytes))
            throw new DocumentRejectedException("WrongContent", $"This file does not look like a real {extension} file. It may be renamed or damaged.");
    }

    private static ReadDocument ReadPdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var pages = new List<DocumentPage>();
            var multiColumn = 0;

            foreach (var page in pdf.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                if (text.Count(char.IsLetter) >= MinPageTextChars)
                {
                    pages.Add(new DocumentPage(page.Number, text));
                    if (LooksMultiColumn(page))
                        multiColumn++;
                    continue;
                }

                // No real text layer: keep the largest picture on the page, which on a scanned page is the page itself.
                var (image, mediaType) = LargestImage(page);
                pages.Add(new DocumentPage(page.Number, string.Empty, image, mediaType));
            }

            var warnings = new List<string>();
            var textPages = pages.Where(p => p.Text.Length > 0).ToList();
            var score = textPages.Count == 0
                ? 1.0
                : PdfDocumentExtractor.Score(string.Join("\n\n", textPages.Select(p => p.Text)), multiColumn, textPages.Count, warnings);

            var scanned = pages.Count(p => p.IsScanned);
            if (scanned > 0)
                warnings.Add($"{scanned} of {pages.Count} pages are scanned images with no text layer.");
            var blank = pages.Count(p => p.Text.Length == 0 && p.Image is null);
            if (blank > 0)
                warnings.Add($"{blank} of {pages.Count} pages have no text and no picture that could be read.");

            return new ReadDocument("pdf", pages, score, warnings);
        }
        catch (DocumentRejectedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentRejectedException("Corrupt", "The PDF could not be read. It may be damaged or password-protected.");
        }
    }

    private static (byte[]? Image, string? MediaType) LargestImage(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            var image = page.GetImages().OrderByDescending(i => i.WidthInSamples * i.HeightInSamples).FirstOrDefault();
            if (image is null)
                return (null, null);
            if (image.TryGetPng(out var png))
                return (png, "image/png");

            // A JPEG embedded as is (most scanners) can be sent without decoding it.
            var raw = image.RawMemory.ToArray();
            return raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 ? (raw, "image/jpeg") : (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    private static bool LooksMultiColumn(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count < 40)
            return false;
        var mid = page.Width / 2;
        var left = words.Count(w => w.BoundingBox.Right < mid - 5);
        var right = words.Count(w => w.BoundingBox.Left > mid + 5);
        return left > words.Count * 0.3 && right > words.Count * 0.3 && words.Count - left - right < words.Count * 0.1;
    }

    /// <summary>The first bytes of each kind of file: a renamed executable or a damaged file never reaches a parser.</summary>
    internal static bool LooksLike(string extension, byte[] bytes)
    {
        bool StartsWith(params byte[] magic) => bytes.Length >= magic.Length && magic.Select((b, i) => bytes[i] == b).All(x => x);

        return extension switch
        {
            ".pdf" => StartsWith(0x25, 0x50, 0x44, 0x46), // %PDF
            ".png" => StartsWith(0x89, 0x50, 0x4E, 0x47),
            ".jpg" or ".jpeg" => StartsWith(0xFF, 0xD8, 0xFF),
            ".docx" => StartsWith(0x50, 0x4B, 0x03, 0x04), // a zip package
            // Text formats: no NUL bytes in the first 8 KB (a binary file renamed .txt has plenty).
            _ => !bytes.Take(8192).Contains((byte)0),
        };
    }
}
