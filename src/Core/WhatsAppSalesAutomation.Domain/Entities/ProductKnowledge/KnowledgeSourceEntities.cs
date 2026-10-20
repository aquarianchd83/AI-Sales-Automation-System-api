using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;

/// <summary>
/// Something a tenant gave the product-knowledge engine to read: an uploaded document (brochure, PDF, manual, price list) or their website.
/// The original file and the extracted text are kept in the platform's AWS bucket under the tenant's own folder; this row holds where, what
/// happened to it, and the fingerprint used to spot the same file uploaded twice. Its passages are the evidence every later answer cites.
/// </summary>
public class KnowledgeSource : BaseEntity, ITenantOwned, ISoftDelete
{
    public Guid TenantId { get; set; }

    public KnowledgeSourceKind Kind { get; set; }

    /// <summary>What the tenant sees: the file name, or the site's host.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A website's start address (normalised). Null for a document.</summary>
    public string? Url { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>SHA-256 of the uploaded bytes (hex). The same tenant uploading the same file again is told it already has it.</summary>
    public string? ContentHash { get; set; }

    /// <summary>The original file in the tenant's AWS folder. Never a public link: read back only through the tenant's own API.</summary>
    public string? StorageKey { get; set; }

    /// <summary>The extracted text of the current version, as a .txt file in the tenant's AWS folder.</summary>
    public string? TextStorageKey { get; set; }

    public KnowledgeSourceStatus Status { get; set; } = KnowledgeSourceStatus.Queued;

    /// <summary>A short machine reason for a failure (UnsupportedType, ScannedReadingOff, Unreachable, ...).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The failure in a sentence the tenant can act on.</summary>
    public string? Error { get; set; }

    /// <summary>Notes that did not stop the source being read ("2 of 9 pages look multi-column").</summary>
    public string? Warnings { get; set; }

    /// <summary>How many times it has been processed (the first run and every retry or re-scan).</summary>
    public int Attempts { get; set; }

    /// <summary>1 for the first reading; +1 for each re-scan or reprocessing that produced new text. Passages belong to the current version.</summary>
    public int Version { get; set; }

    public int PassageCount { get; set; }

    /// <summary>Pages in a document, or pages read on a website.</summary>
    public int PageCount { get; set; }

    /// <summary>0..1: how confident the reading is that order and structure survived (PDF columns lower it).</summary>
    public double? QualityScore { get; set; }

    /// <summary>True when some pages were scanned images read with the AI provider rather than a text layer.</summary>
    public bool UsedAiReading { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>One page of a website source as the crawler found it: read, skipped as a duplicate, refused by robots.txt, or failed.</summary>
public class KnowledgeSourcePage : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid SourceId { get; set; }

    public int SourceVersion { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? Title { get; set; }

    public KnowledgeSourcePageStatus Status { get; set; }

    public int? HttpStatus { get; set; }

    public string? Error { get; set; }

    /// <summary>SHA-256 of the page's extracted text: two addresses with the same text are one page.</summary>
    public string? ContentHash { get; set; }

    public int Depth { get; set; }

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// The unit of evidence: a passage of a source's text with exactly where it came from - the PDF page, the heading it sits under, or the
/// page address and when it was read. Later answers cite passages by id, and their quotes are checked against <see cref="Text"/>.
/// </summary>
public class SourcePassage : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid SourceId { get; set; }

    public int SourceVersion { get; set; }

    /// <summary>0, 1, 2... in reading order across the whole source.</summary>
    public int Order { get; set; }

    /// <summary>1-based page in a PDF. Null for formats without pages and for websites.</summary>
    public int? PageNumber { get; set; }

    /// <summary>The website page it came from. Null for a document.</summary>
    public string? Url { get; set; }

    /// <summary>The nearest heading above it, for "Pricing &gt; Plans" style references.</summary>
    public string? Heading { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>SHA-256 of the normalised text, so a re-scan can tell which passages changed.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>True when the text came from reading a scanned image with the AI provider (it is a reading, not a text layer).</summary>
    public bool FromAiReading { get; set; }

    /// <summary>When the text was taken from its source - the extraction time, as the requirements ask for websites.</summary>
    public DateTime ExtractedAt { get; set; }
}
