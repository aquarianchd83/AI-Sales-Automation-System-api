namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

// ----- settings ----------------------------------------------------------------------------------------------------------------------

/// <summary>The platform's limits for product-knowledge sources (the "ProductKnowledge:*" settings), read fresh for each request.</summary>
public sealed record SourceLimits(
    int MaxFileMb,
    int MaxSourcesPerTenant,
    int MaxScannedPages,
    int CrawlMaxPages,
    int CrawlMaxDepth,
    int CrawlPageTimeoutSeconds,
    int CrawlMaxPageKb,
    bool CrawlRespectRobots,
    bool DocumentUploadEnabled = true,
    bool WebsiteScanningEnabled = true,
    int DraftPassages = 6,
    int MaxCustomQuestions = 20)
{
    public const string DocumentUploadOffMessage =
        "Uploading documents is switched off on this platform for now. Documents already read stay available. Ask your platform administrator if you need it.";

    public const string WebsiteScanningOffMessage =
        "Reading websites is switched off on this platform for now. Pages already read stay available. Ask your platform administrator if you need it.";

    public long MaxFileBytes => MaxFileMb * 1024L * 1024L;
}

public interface ISourceLimitsProvider
{
    SourceLimits Get();
}

// ----- private file storage in AWS ---------------------------------------------------------------------------------------------------

/// <summary>
/// The tenant's private files for the product-knowledge engine, kept in the platform's AWS bucket under the tenant's own folder:
/// <c>{prefix}/{tenantId}/product-knowledge/...</c>. Unlike media for WhatsApp, nothing here is ever a public link - a file is read back only
/// through the tenant's own API, after the tenant check. Written encrypted at rest.
/// </summary>
public interface ITenantDocumentStore
{
    /// <summary>False when the AWS Settings (bucket, region) are not filled in: uploads are refused with a sentence saying so.</summary>
    bool IsConfigured { get; }

    /// <param name="relativePath">Under the tenant's product-knowledge folder, e.g. <c>sources/{id}/original.pdf</c>.</param>
    Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>The object key a tenant's file would get, so a key can be checked to belong to that tenant before it is read.</summary>
    string FolderOf(Guid tenantId);
}

// ----- reading documents ---------------------------------------------------------------------------------------------------------------

/// <summary>One page of a document. <see cref="Number"/> is the 1-based PDF page, or null for formats without pages. A scanned page has
/// no text layer: <see cref="Text"/> is empty and <see cref="Image"/> holds the page picture to be read with the AI provider, if allowed.</summary>
public sealed record DocumentPage(int? Number, string Text, byte[]? Image = null, string? ImageMediaType = null)
{
    public bool IsScanned => string.IsNullOrWhiteSpace(Text) && Image is not null;
}

public sealed record ReadDocument(string Format, IReadOnlyList<DocumentPage> Pages, double QualityScore, IReadOnlyList<string> Warnings);

/// <summary>Reads a document into pages, keeping page numbers where the format has them (PDF). Throws
/// <see cref="KnowledgeBase.Ingestion.DocumentRejectedException"/> for a file that cannot be read.</summary>
public interface IPagedDocumentReader
{
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Refuses a file whose type is not supported or whose bytes are not what its name says - before it is stored.</summary>
    void EnsureAcceptable(string fileName, byte[] bytes);

    ReadDocument Read(string fileName, Stream content);
}

// ----- reading websites ----------------------------------------------------------------------------------------------------------------

public sealed record CrawlOptions(int MaxPages, int MaxDepth, int PageTimeoutSeconds, long MaxPageBytes, bool RespectRobots);

/// <param name="Status">"Read", "Blocked" (robots.txt), "Failed", "Skipped" (not a web page, too big, other site).</param>
public sealed record CrawledPage(string Url, int Depth, string Status, int? HttpStatus, string? Title, string? Markdown, string? Error);

/// <param name="Error">Set when the site could not be scanned at all (bad address, private network, nothing reachable).</param>
public sealed record CrawlResult(string StartUrl, IReadOnlyList<CrawledPage> Pages, string? Error);

/// <summary>
/// Reads a tenant's public website: same site only, limited depth and page count, robots.txt respected, web pages only, and never anything
/// on a private or internal network - every address is checked when the connection is made, including after each redirect.
/// </summary>
public interface IWebsiteCrawler
{
    Task<CrawlResult> CrawlAsync(string startUrl, CrawlOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Puts a source in line to be read in the background.</summary>
public interface IKnowledgeSourceQueue
{
    void Enqueue(Guid sourceId, Guid tenantId);
}

/// <summary>A website address that cannot be used, said in a sentence the tenant can act on.</summary>
public sealed class UnsafeUrlException : Exception
{
    public UnsafeUrlException(string message) : base(message)
    {
    }
}
