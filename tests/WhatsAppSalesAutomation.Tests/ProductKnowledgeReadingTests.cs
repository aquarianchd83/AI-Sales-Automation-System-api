using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Reading documents into page-referenced passages: page numbers kept, scanned pages recognised, files checked to be what they say.
/// The PDFs are built in the test with PdfPig's own writer.</summary>
public sealed class ProductKnowledgeReadingTests
{
    /// <summary>A 1x1 PNG - enough to put a "scanned" picture on a page with no text layer.</summary>
    internal static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A PDF whose pages are the given lines of text; a null page is a scanned page (a picture, no text).</summary>
    internal static byte[] Pdf(params string?[]? pages)
    {
        // Pdf(null) is one scanned page: C# hands a lone null to a params parameter as a null array.
        pages ??= new string?[] { null };
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text is null)
            {
                page.AddPng(TinyPng, new PdfRectangle(0, 0, 595, 842));
                continue;
            }

            var y = 780.0;
            foreach (var line in text.Split('\n'))
            {
                page.AddText(line, 11, new PdfPoint(40, y), font);
                y -= 16;
            }
        }
        return builder.Build();
    }

    private static PagedDocumentReader Reader() =>
        new(new IDocumentFormatExtractor[] { new TextDocumentExtractor(), new HtmlDocumentExtractor(), new DocxDocumentExtractor() });

    // ----- PDFs ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pdf_is_read_page_by_page_and_keeps_its_page_numbers()
    {
        var bytes = Pdf("Consultation fee is Rs 500 for the first visit to the clinic.", "Cataract surgery starts at Rs 25,000 per eye at our hospital.");

        var document = Reader().Read("brochure.pdf", new MemoryStream(bytes));

        Assert.Equal("pdf", document.Format);
        Assert.Equal(new int?[] { 1, 2 }, document.Pages.Select(p => p.Number));
        Assert.Contains("Rs 500", document.Pages[0].Text);
        Assert.Contains("25,000", document.Pages[1].Text);
        Assert.All(document.Pages, p => Assert.False(p.IsScanned));
    }

    [Fact]
    public void A_page_with_no_text_layer_but_a_picture_is_a_scanned_page_and_its_picture_is_kept()
    {
        var bytes = Pdf("Our clinic is open Monday to Saturday from nine in the morning.", null);

        var document = Reader().Read("mixed.pdf", new MemoryStream(bytes));

        Assert.False(document.Pages[0].IsScanned);
        var scanned = document.Pages[1];
        Assert.True(scanned.IsScanned);
        Assert.Equal(2, scanned.Number);
        Assert.NotNull(scanned.Image);
        Assert.Equal("image/png", scanned.ImageMediaType);
        Assert.Contains(document.Warnings, w => w.Contains("1 of 2 pages are scanned"));
    }

    [Fact]
    public void A_photo_of_a_document_is_one_scanned_page()
    {
        var document = Reader().Read("menu.png", new MemoryStream(TinyPng));

        var page = Assert.Single(document.Pages);
        Assert.True(page.IsScanned);
        Assert.Equal("image/png", page.ImageMediaType);
    }

    [Fact]
    public void Other_formats_are_one_page_without_a_number()
    {
        var document = Reader().Read("faq.txt", new MemoryStream(Encoding.UTF8.GetBytes("Do you deliver?\n\nYes, across Pune within two days.")));

        var page = Assert.Single(document.Pages);
        Assert.Null(page.Number);
        Assert.Contains("across Pune", page.Text);
    }

    // ----- checking the file is what it says ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("notes.exe", "UnsupportedType")]
    [InlineData("noextension", "UnsupportedType")]
    public void Unsupported_types_are_refused(string fileName, string code)
    {
        var ex = Assert.Throws<DocumentRejectedException>(() => Reader().EnsureAcceptable(fileName, new byte[] { 1, 2, 3 }));
        Assert.Equal(code, ex.ReasonCode);
    }

    [Fact]
    public void A_renamed_file_is_refused_before_any_parser_sees_it()
    {
        var notAPdf = Encoding.UTF8.GetBytes("MZ this is really an executable");
        Assert.Equal("WrongContent", Assert.Throws<DocumentRejectedException>(() => Reader().EnsureAcceptable("price-list.pdf", notAPdf)).ReasonCode);

        var binaryAsText = new byte[] { 0x48, 0x00, 0x00, 0x01 };
        Assert.Equal("WrongContent", Assert.Throws<DocumentRejectedException>(() => Reader().EnsureAcceptable("faq.txt", binaryAsText)).ReasonCode);

        Assert.Equal("WrongContent", Assert.Throws<DocumentRejectedException>(() => Reader().EnsureAcceptable("scan.jpg", TinyPng)).ReasonCode);
        Assert.Equal("Empty", Assert.Throws<DocumentRejectedException>(() => Reader().EnsureAcceptable("empty.pdf", Array.Empty<byte>())).ReasonCode);
    }

    [Fact]
    public void A_damaged_pdf_is_refused_with_a_plain_reason()
    {
        var damaged = Encoding.ASCII.GetBytes("%PDF-1.7 then nothing that makes sense");

        var ex = Assert.Throws<DocumentRejectedException>(() => Reader().Read("broken.pdf", new MemoryStream(damaged)));

        Assert.Equal("Corrupt", ex.ReasonCode);
        Assert.Contains("damaged or password-protected", ex.Message);
    }

    // ----- passages ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Passages_keep_the_heading_path_they_sit_under()
    {
        var text = "# Eye Care Clinic\n\nWe treat adults and children.\n\n## Pricing\n\nConsultation is Rs 500.\n\n### Surgery\n\nCataract starts at Rs 25,000 per eye.\n\n## Timings\n\nMonday to Saturday, 9 to 6.";

        var passages = PassageSplitter.Split(text);

        Assert.Equal(new[] { "Eye Care Clinic", "Eye Care Clinic > Pricing", "Eye Care Clinic > Pricing > Surgery", "Eye Care Clinic > Timings" },
            passages.Select(p => p.Heading));
        Assert.DoesNotContain(passages, p => p.Text.StartsWith('#'));
    }

    [Fact]
    public void Long_text_is_cut_at_sentence_ends_and_no_passage_is_too_long_to_quote()
    {
        var sentence = "Our hospital has twenty years of experience in eye care across Maharashtra. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 80));

        var passages = PassageSplitter.Split(text);

        Assert.True(passages.Count > 1);
        Assert.All(passages, p => Assert.True(p.Text.Length <= PassageSplitter.MaxChars, $"{p.Text.Length} chars"));
        Assert.All(passages, p => Assert.EndsWith(".", p.Text));
        Assert.Equal(PassageSplitter.Normalise(text), PassageSplitter.Normalise(string.Join(" ", passages.Select(p => p.Text))));
    }

    [Fact]
    public void A_tiny_leftover_joins_the_passage_before_it_and_nothing_is_lost()
    {
        var text = string.Concat(Enumerable.Repeat("A paragraph about the services we offer to our patients. ", 16)) + "\n\nCall us.";

        var passages = PassageSplitter.Split(text);

        Assert.EndsWith("Call us.", passages[^1].Text);
        Assert.DoesNotContain(passages, p => p.Text == "Call us.");
    }

    [Fact]
    public void Passages_that_differ_only_in_spacing_or_case_have_the_same_fingerprint()
    {
        Assert.Equal(PassageSplitter.HashOf("Fee is  Rs 500.\n"), PassageSplitter.HashOf("fee is rs 500."));
        Assert.NotEqual(PassageSplitter.HashOf("Fee is Rs 500."), PassageSplitter.HashOf("Fee is Rs 600."));
        Assert.Empty(PassageSplitter.Split("   \n\n  "));
    }
}
