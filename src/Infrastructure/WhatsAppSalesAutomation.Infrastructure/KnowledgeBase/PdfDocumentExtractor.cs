using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// PDF text via PdfPig (G.3). Lives in Infrastructure so the Application layer carries no PDF library.
///
/// A PDF has no structure to read, only glyphs at positions, so extraction quality is a guess - and
/// the failure that matters is reading order. A two-column page read straight across the columns
/// interleaves two unrelated sentences, which chunks and embeds perfectly happily. The
/// <see cref="ExtractionResult.QualityScore"/> exists so that guess is made visible: below the
/// threshold the article is not auto-approved and a human is shown the result.
///
/// Scanned PDFs (no text layer) are rejected. OCR is deliberately out of scope for Phase 6.
/// </summary>
public sealed class PdfDocumentExtractor : IDocumentFormatExtractor
{
    public IReadOnlyList<string> Extensions { get; } = new[] { ".pdf" };

    public ExtractionResult Extract(Stream content)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);
            var pages = new List<string>();
            var multiColumnPages = 0;

            foreach (var page in pdf.GetPages())
            {
                pages.Add(ContentOrderTextExtractor.GetText(page));

                if (LooksMultiColumn(page))
                    multiColumnPages++;
            }

            var text = ContentSanitizer.RemoveRepeatingBoilerplate(string.Join("\n\n", pages));

            if (text.Trim().Length == 0)
                throw new DocumentRejectedException("NoTextLayer", "This PDF has no text layer (it looks scanned). OCR is not supported.");

            var warnings = new List<string>();
            var score = Score(text, multiColumnPages, pages.Count, warnings);

            return new ExtractionResult(text.Trim(), "pdf", score, warnings);
        }
        catch (DocumentRejectedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentRejectedException("Corrupt", "The PDF could not be read: " + ex.Message);
        }
    }

    /// <summary>A page whose words start in two well-separated horizontal bands, with a real gap
    /// between them, is probably set in columns.</summary>
    private static bool LooksMultiColumn(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count < 40)
            return false;

        var mid = page.Width / 2;
        var left = words.Count(w => w.BoundingBox.Right < mid - 5);
        var right = words.Count(w => w.BoundingBox.Left > mid + 5);
        var straddling = words.Count - left - right;

        return left > words.Count * 0.3 && right > words.Count * 0.3 && straddling < words.Count * 0.1;
    }

    /// <summary>Starts at 1 and loses points for the two signs of a bad extraction: columns, and lines
    /// so short and unpunctuated that they are probably fragments of a broken flow.</summary>
    internal static double Score(string text, int multiColumnPages, int pageCount, List<string> warnings)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var orphanRatio = lines.Count == 0
            ? 0
            : (double)lines.Count(l => l.Length < 25 && !".:!?;)".Contains(l[^1])) / lines.Count;

        var score = 1.0 - Math.Min(0.5, orphanRatio * 0.8);

        if (multiColumnPages > 0 && pageCount > 0)
        {
            score -= 0.4 * multiColumnPages / pageCount;
            warnings.Add($"{multiColumnPages} of {pageCount} pages look multi-column; reading order may be wrong.");
        }

        return Math.Max(0, Math.Round(score, 2));
    }
}
