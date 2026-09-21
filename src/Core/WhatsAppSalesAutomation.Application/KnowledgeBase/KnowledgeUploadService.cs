using System.Text.RegularExpressions;
using FluentValidation;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;

namespace WhatsAppSalesAutomation.Application.KnowledgeBase;

/// <param name="Title">Optional. Defaults to the document's first heading, then to its file name.</param>
public record UploadKnowledgeArticleRequest(string? Title, string? Category, string? SourceType);

/// <param name="QualityScore">How confident extraction is that reading order and structure survived (1.0 for
/// formats that carry their own). A low score means the text should be read before it is published.</param>
public record UploadKnowledgeArticleResultDto(
    KnowledgeBaseArticleDto Article,
    string Format,
    double QualityScore,
    bool NeedsHumanReview,
    IReadOnlyList<string> Warnings);

public interface IKnowledgeUploadService
{
    Task<UploadKnowledgeArticleResultDto> UploadAsync(
        string fileName, long length, Stream content, UploadKnowledgeArticleRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns an uploaded document into a DRAFT article.
///
/// Draft, never published: an upload is somebody's file, and the point of the publish step is that a
/// person reads what was extracted before it becomes something the AI will quote. That matters most for
/// PDF, where extraction is a guess and a wrong reading order can put half a rule in the wrong place -
/// hence the quality score returned alongside the article, so the caller can say "check this one".
///
/// Goes through <see cref="IKnowledgeBaseService.CreateAsync"/> rather than writing an article itself,
/// so an upload obeys exactly the same rules as one typed in: the plan's article limit, the source-type
/// authority rules, the tenant stamping.
/// </summary>
public sealed class KnowledgeUploadService : IKnowledgeUploadService
{
    private const int MaxTitleLength = 200;
    private const string DefaultSourceType = "AdminConfiguredArticle";

    private static readonly Regex FirstHeading = new(@"^#{1,3}\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly DocumentTextExtractor _extractor;
    private readonly IKnowledgeBaseService _knowledgeBase;

    public KnowledgeUploadService(DocumentTextExtractor extractor, IKnowledgeBaseService knowledgeBase)
    {
        _extractor = extractor;
        _knowledgeBase = knowledgeBase;
    }

    public async Task<UploadKnowledgeArticleResultDto> UploadAsync(
        string fileName, long length, Stream content, UploadKnowledgeArticleRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw Invalid("file", "A file is required.");

        ExtractionResult extracted;
        try
        {
            extracted = _extractor.Extract(fileName, length, content);
        }
        catch (DocumentRejectedException ex)
        {
            // A content decision, reported as a 400 with the reason - not an unhandled 500.
            throw Invalid("file", ex.Message);
        }

        var title = ChooseTitle(request.Title, extracted.Markdown, fileName);

        var article = await _knowledgeBase.CreateAsync(
            new CreateKnowledgeBaseArticleRequest(
                title,
                string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
                extracted.Markdown,
                string.IsNullOrWhiteSpace(request.SourceType) ? DefaultSourceType : request.SourceType.Trim()),
            cancellationToken);

        return new UploadKnowledgeArticleResultDto(article, extracted.Format, extracted.QualityScore, extracted.NeedsHumanReview, extracted.Warnings);
    }

    /// <summary>The title the author gave, else the document's own first heading, else the file name
    /// made readable. A title the user typed always wins - they know what it is called.</summary>
    internal static string ChooseTitle(string? requested, string markdown, string fileName)
    {
        string title;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            title = requested.Trim();
        }
        else if (FirstHeading.Match(markdown) is { Success: true } heading)
        {
            title = heading.Groups[1].Value.Trim();
        }
        else
        {
            title = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"[_\-]+", " ").Trim();
        }

        if (title.Length == 0)
            title = "Untitled article";

        return title.Length > MaxTitleLength ? title[..MaxTitleLength].TrimEnd() : title;
    }

    private static ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
