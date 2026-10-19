using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Enums;
using ValidationFailure = FluentValidation.Results.ValidationFailure;

namespace WhatsAppSalesAutomation.Application.ProductKnowledge;

public record KnowledgeSourceDto(
    Guid Id, string Kind, string Name, string? Url, string? FileName, long? SizeBytes, string Status, string? ErrorCode, string? Error,
    IReadOnlyList<string> Warnings, int Attempts, int Version, int PassageCount, int PageCount, double? QualityScore, bool UsedAiReading,
    DateTime CreatedAt, DateTime? ProcessedAt, bool CanRetry, bool CanRescan, bool HasOriginal, bool HasText);

public record KnowledgeSourcePageDto(string Url, string? Title, string Status, int? HttpStatus, string? Error, int Depth, DateTime FetchedAt);

public record SourcePassageDto(Guid Id, int Order, int? PageNumber, string? Url, string? Heading, string Text, bool FromAiReading, DateTime ExtractedAt);

public record KnowledgeSourceDetailDto(KnowledgeSourceDto Source, IReadOnlyList<KnowledgeSourcePageDto> Pages, IReadOnlyList<SourcePassageDto> Passages, int PassageTotal);

/// <param name="ScannedReading">"Off", "Simulator" or "Api": how scanned pages are read right now (the platform's switch on Integrations).</param>
/// <param name="DocumentUploadEnabled">The platform's "Document upload" switch on Integrations.</param>
/// <param name="WebsiteScanningEnabled">The platform's "Website scanning" switch on Integrations.</param>
public record KnowledgeSourcesOverviewDto(
    IReadOnlyList<KnowledgeSourceDto> Sources, int MaxSources, int MaxFileMb, IReadOnlyList<string> SupportedExtensions, string ScannedReading, bool StorageReady,
    bool DocumentUploadEnabled = true, bool WebsiteScanningEnabled = true);

public record AddWebsiteSourceRequest(string Url);

/// <summary>A stored file handed back to the tenant through the API (never a public link).</summary>
public record SourceFile(Stream Content, string FileName, string ContentType);

public interface IKnowledgeSourceService
{
    Task<KnowledgeSourcesOverviewDto> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<KnowledgeSourceDetailDto> GetAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default);

    Task<KnowledgeSourceDto> UploadAsync(Guid tenantId, Guid? userId, string fileName, string? contentType, Stream content, long length, CancellationToken cancellationToken = default);

    Task<KnowledgeSourceDto> AddWebsiteAsync(Guid tenantId, Guid? userId, AddWebsiteSourceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a failed source again, or re-scans a website (a new version of its passages).</summary>
    Task<KnowledgeSourceDto> RetryAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default);

    /// <param name="extracted">True: the extracted text file; false: the original upload.</param>
    Task<SourceFile> OpenFileAsync(Guid tenantId, Guid sourceId, bool extracted, CancellationToken cancellationToken = default);
}

/// <summary>
/// The tenant's sources for the product-knowledge engine: upload a document, add the website, see how each was read, retry a failure,
/// re-scan the site, remove a source. Every query names the tenant explicitly as well as relying on the tenant filter, and a source of
/// another tenant is "not found". Reading happens in the background (<see cref="IKnowledgeSourceProcessor"/>); this service only checks,
/// stores and queues.
/// </summary>
public sealed class KnowledgeSourceService : IKnowledgeSourceService
{
    private const int PassagePreviewLimit = 200;

    private readonly IApplicationDbContext _db;
    private readonly ITenantDocumentStore _store;
    private readonly IPagedDocumentReader _reader;
    private readonly IKnowledgeSourceQueue _queue;
    private readonly ISourceLimitsProvider _limits;
    private readonly IAiFeatureRouting _routing;

    public KnowledgeSourceService(
        IApplicationDbContext db, ITenantDocumentStore store, IPagedDocumentReader reader, IKnowledgeSourceQueue queue, ISourceLimitsProvider limits,
        IAiFeatureRouting routing)
    {
        _db = db;
        _store = store;
        _reader = reader;
        _queue = queue;
        _limits = limits;
        _routing = routing;
    }

    public async Task<KnowledgeSourcesOverviewDto> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var limits = _limits.Get();
        var sources = await _db.KnowledgeSources.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var scanned = _routing.Resolve(AiFeatures.ProductScannedReading);
        var scannedMode = scanned.IsOff ? AiFeatures.OffMode : scanned.UsesThirdParty ? PlatformIntegrationSettingsService.ApiMode : PlatformIntegrationSettingsService.SimulatorMode;

        return new KnowledgeSourcesOverviewDto(
            sources.Select(ToDto).ToList(), limits.MaxSourcesPerTenant, limits.MaxFileMb, _reader.SupportedExtensions, scannedMode, _store.IsConfigured,
            limits.DocumentUploadEnabled, limits.WebsiteScanningEnabled);
    }

    public async Task<KnowledgeSourceDetailDto> GetAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        var source = await FindAsync(tenantId, sourceId, cancellationToken);

        var pages = await _db.KnowledgeSourcePages.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.SourceId == sourceId && p.SourceVersion == source.Version)
            .OrderBy(p => p.Depth).ThenBy(p => p.FetchedAt)
            .Select(p => new KnowledgeSourcePageDto(p.Url, p.Title, p.Status.ToString(), p.HttpStatus, p.Error, p.Depth, p.FetchedAt))
            .ToListAsync(cancellationToken);

        var passages = _db.SourcePassages.AsNoTracking().Where(p => p.TenantId == tenantId && p.SourceId == sourceId && p.SourceVersion == source.Version);
        var total = await passages.CountAsync(cancellationToken);
        var preview = await passages.OrderBy(p => p.Order).Take(PassagePreviewLimit)
            .Select(p => new SourcePassageDto(p.Id, p.Order, p.PageNumber, p.Url, p.Heading, p.Text, p.FromAiReading, p.ExtractedAt))
            .ToListAsync(cancellationToken);

        return new KnowledgeSourceDetailDto(ToDto(source), pages, preview, total);
    }

    public async Task<KnowledgeSourceDto> UploadAsync(
        Guid tenantId, Guid? userId, string fileName, string? contentType, Stream content, long length, CancellationToken cancellationToken = default)
    {
        var limits = _limits.Get();
        if (!limits.DocumentUploadEnabled)
            throw new FeatureDisabledException(SourceLimits.DocumentUploadOffMessage);
        fileName = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (fileName.Length == 0)
            throw Invalid("file", "Choose a file to upload.");
        if (fileName.Length > 255)
            fileName = fileName[^255..];
        if (length > limits.MaxFileBytes)
            throw Invalid("file", $"The file is {Mb(length)} MB; the most you can upload is {limits.MaxFileMb} MB.");

        await EnsureRoomAsync(tenantId, limits, cancellationToken);
        EnsureStorage();

        // Read it once, capped, so the hash, the type check and the stored copy are all of the same bytes.
        var bytes = await ReadCappedAsync(content, limits.MaxFileBytes, cancellationToken)
            ?? throw Invalid("file", $"The file is larger than {limits.MaxFileMb} MB.");
        try
        {
            _reader.EnsureAcceptable(fileName, bytes);
        }
        catch (DocumentRejectedException ex)
        {
            throw Invalid("file", ex.Message);
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await _db.KnowledgeSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ContentHash == hash, cancellationToken);
        if (existing is not null)
            throw new ConflictException($"You already uploaded this file as \"{existing.Name}\" on {existing.CreatedAt:d MMM yyyy}. It is not read twice.");

        var source = new KnowledgeSource
        {
            TenantId = tenantId,
            Kind = KnowledgeSourceKind.Document,
            Name = fileName,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            SizeBytes = bytes.LongLength,
            ContentHash = hash,
            Status = KnowledgeSourceStatus.Queued,
            CreatedBy = userId,
        };

        using (var stream = new MemoryStream(bytes))
            source.StorageKey = await _store.SaveAsync(tenantId, $"sources/{source.Id:N}/original{Path.GetExtension(fileName).ToLowerInvariant()}", stream, source.ContentType, cancellationToken);

        _db.KnowledgeSources.Add(source);
        await _db.SaveChangesAsync(cancellationToken);
        _queue.Enqueue(source.Id, tenantId);
        return ToDto(source);
    }

    public async Task<KnowledgeSourceDto> AddWebsiteAsync(Guid tenantId, Guid? userId, AddWebsiteSourceRequest request, CancellationToken cancellationToken = default)
    {
        var limits = _limits.Get();
        if (!limits.WebsiteScanningEnabled)
            throw new FeatureDisabledException(SourceLimits.WebsiteScanningOffMessage);
        var url = NormaliseWebsite(request?.Url);

        await EnsureRoomAsync(tenantId, limits, cancellationToken);
        EnsureStorage();

        var existing = await _db.KnowledgeSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Kind == KnowledgeSourceKind.Website && s.Url == url.AbsoluteUri, cancellationToken);
        if (existing is not null)
            throw new ConflictException($"This website is already one of your sources. Use \"Scan again\" on it to read it afresh.");

        var source = new KnowledgeSource
        {
            TenantId = tenantId,
            Kind = KnowledgeSourceKind.Website,
            Name = url.Host,
            Url = url.AbsoluteUri,
            Status = KnowledgeSourceStatus.Queued,
            CreatedBy = userId,
        };

        _db.KnowledgeSources.Add(source);
        await _db.SaveChangesAsync(cancellationToken);
        _queue.Enqueue(source.Id, tenantId);
        return ToDto(source);
    }

    public async Task<KnowledgeSourceDto> RetryAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        var source = await FindTrackedAsync(tenantId, sourceId, cancellationToken);
        var limits = _limits.Get();
        if (source.Kind == KnowledgeSourceKind.Website && !limits.WebsiteScanningEnabled)
            throw new FeatureDisabledException(SourceLimits.WebsiteScanningOffMessage);
        if (source.Kind == KnowledgeSourceKind.Document && !limits.DocumentUploadEnabled)
            throw new FeatureDisabledException(SourceLimits.DocumentUploadOffMessage);
        if (!CanRetry(source) && !CanRescan(source))
            throw new ConflictException(source.Status is KnowledgeSourceStatus.Queued or KnowledgeSourceStatus.Processing
                ? "This source is being read right now."
                : "This document has been read already. To change it, upload the new version.");

        source.Status = KnowledgeSourceStatus.Queued;
        source.ErrorCode = null;
        source.Error = null;
        await _db.SaveChangesAsync(cancellationToken);
        _queue.Enqueue(source.Id, tenantId);
        return ToDto(source);
    }

    public async Task DeleteAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        var source = await FindTrackedAsync(tenantId, sourceId, cancellationToken);

        // The tenant's copy in AWS goes too; the database row is kept (soft-deleted) for the audit of what the knowledge was built from.
        foreach (var key in new[] { source.StorageKey, source.TextStorageKey }.Where(k => !string.IsNullOrEmpty(k)))
        {
            try
            {
                await _store.DeleteAsync(key!, cancellationToken);
            }
            catch (StorageUnavailableException)
            {
                // Removing the source from the tenant's view must not wait on storage; the file is unreachable through the API either way.
            }
        }

        var passages = await _db.SourcePassages.Where(p => p.TenantId == tenantId && p.SourceId == sourceId).ToListAsync(cancellationToken);
        var pages = await _db.KnowledgeSourcePages.Where(p => p.TenantId == tenantId && p.SourceId == sourceId).ToListAsync(cancellationToken);
        _db.SourcePassages.RemoveRange(passages);
        _db.KnowledgeSourcePages.RemoveRange(pages);

        source.IsDeleted = true;
        source.DeletedAt = DateTime.UtcNow;
        source.StorageKey = null;
        source.TextStorageKey = null;
        // A deleted file may be uploaded again later, so its fingerprint no longer blocks it.
        source.ContentHash = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SourceFile> OpenFileAsync(Guid tenantId, Guid sourceId, bool extracted, CancellationToken cancellationToken = default)
    {
        var source = await FindAsync(tenantId, sourceId, cancellationToken);
        var key = extracted ? source.TextStorageKey : source.StorageKey;
        if (string.IsNullOrEmpty(key))
            throw new NotFoundException(extracted ? "There is no extracted text for this source yet." : "This source has no uploaded file.");

        // Defence in depth: the key must sit in this tenant's own folder, whatever the row says.
        if (!key.Contains(_store.FolderOf(tenantId), StringComparison.Ordinal))
            throw new NotFoundException("KnowledgeSource", sourceId);

        try
        {
            var stream = await _store.OpenReadAsync(key, cancellationToken);
            var baseName = Path.GetFileNameWithoutExtension(source.FileName ?? source.Name);
            return extracted
                ? new SourceFile(stream, $"{baseName}-extracted-v{source.Version}.txt", "text/plain; charset=utf-8")
                : new SourceFile(stream, source.FileName ?? "document", source.ContentType ?? "application/octet-stream");
        }
        catch (FileNotFoundException)
        {
            throw new NotFoundException("The file is no longer in storage.");
        }
    }

    // ----- helpers -----------------------------------------------------------------------------------------------------------------

    private async Task<KnowledgeSource> FindAsync(Guid tenantId, Guid sourceId, CancellationToken ct) =>
        await _db.KnowledgeSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId, ct)
        ?? throw new NotFoundException("KnowledgeSource", sourceId);

    private async Task<KnowledgeSource> FindTrackedAsync(Guid tenantId, Guid sourceId, CancellationToken ct) =>
        await _db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId, ct)
        ?? throw new NotFoundException("KnowledgeSource", sourceId);

    private async Task EnsureRoomAsync(Guid tenantId, SourceLimits limits, CancellationToken ct)
    {
        var count = await _db.KnowledgeSources.CountAsync(s => s.TenantId == tenantId, ct);
        if (count >= limits.MaxSourcesPerTenant)
            throw Invalid("sources", $"You have {count} sources, the most allowed. Remove one you no longer need to add another.");
    }

    private void EnsureStorage()
    {
        if (!_store.IsConfigured)
            throw new StorageUnavailableException("Documents are kept in the platform's AWS storage, which is not set up yet. Ask the platform administrator to fill in the AWS Settings.");
    }

    /// <summary>The website address as stored: https assumed when no scheme is typed, lowercase host, no fragment. Only checks the form here;
    /// whether it is a public address is checked by the crawler when it connects.</summary>
    internal static Uri NormaliseWebsite(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw Invalid("url", "Enter your website address, for example https://www.example.com.");
        if (text.Length > 2000)
            throw Invalid("url", "That address is too long.");
        if (!text.Contains("://"))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw Invalid("url", "That is not a website address. Use the form https://www.example.com.");
        if (!uri.Host.Contains('.') || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw Invalid("url", "Use your public website address, for example https://www.example.com.");
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            throw Invalid("url", "Use your website's name (www.example.com), not a numeric address.");

        var builder = new UriBuilder(uri) { Fragment = string.Empty, Host = uri.Host.ToLowerInvariant(), Port = -1 };
        if (builder.Path.Length == 0)
            builder.Path = "/";
        return builder.Uri;
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream content, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.#");

    /// <summary>A run that crashed mid-way leaves a source in Processing; after this long it may be retried.</summary>
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    private static bool CanRetry(KnowledgeSource s) =>
        s.Status == KnowledgeSourceStatus.Failed
        || (s.Status is KnowledgeSourceStatus.Processing or KnowledgeSourceStatus.Queued && (s.UpdatedAt ?? s.CreatedAt) < DateTime.UtcNow - StuckAfter);

    private static bool CanRescan(KnowledgeSource s) => s.Kind == KnowledgeSourceKind.Website && s.Status == KnowledgeSourceStatus.Ready;

    internal static KnowledgeSourceDto ToDto(KnowledgeSource s) => new(
        s.Id, s.Kind.ToString(), s.Name, s.Url, s.FileName, s.SizeBytes, s.Status.ToString(), s.ErrorCode, s.Error,
        string.IsNullOrEmpty(s.Warnings) ? Array.Empty<string>() : s.Warnings.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        s.Attempts, s.Version, s.PassageCount, s.PageCount, s.QualityScore, s.UsedAiReading, s.CreatedAt, s.ProcessedAt,
        CanRetry(s), CanRescan(s), !string.IsNullOrEmpty(s.StorageKey), !string.IsNullOrEmpty(s.TextStorageKey));

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
