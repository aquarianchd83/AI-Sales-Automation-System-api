using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Media;

namespace WhatsAppSalesAutomation.Application.Media;

public class MediaService : IMediaService
{
    private readonly IApplicationDbContext _context;
    private readonly IMediaStorageService _storage;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly IMediaUrlFetcher? _urlFetcher;

    public MediaService(IApplicationDbContext context, IMediaStorageService storage, ITenantConfigOverrideProvider tenantConfig, IMediaUrlFetcher? urlFetcher = null)
    {
        _context = context;
        _storage = storage;
        _tenantConfig = tenantConfig;
        _urlFetcher = urlFetcher;
    }

    public async Task<PagedResult<MediaAssetDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default)
    {
        var query = _context.MediaAssets.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(m => m.FileName.Contains(search));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<MediaAssetDto>(items.Select(ToDto).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<MediaAssetDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await FindOrThrowAsync(id, cancellationToken));

    public async Task<MediaAssetDto> UploadAsync(
        Stream content,
        string fileName,
        string contentType,
        long sizeBytes,
        Guid? uploadedBy,
        CancellationToken cancellationToken = default)
    {
        if (sizeBytes <= 0)
            throw Invalid(nameof(content), "The uploaded file is empty.");

        // Resolved per call (not once per DI scope) - merges this tenant's Media:* overrides, if any,
        // over the platform default. See ITenantConfigOverrideProvider's own doc comment.
        var options = await _tenantConfig.GetMediaOptionsAsync(cancellationToken);

        if (sizeBytes > options.MaxSizeBytes)
            throw Invalid(nameof(sizeBytes), $"File exceeds the {options.MaxSizeBytes / (1024 * 1024)} MB limit.");

        if (!options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw Invalid(nameof(contentType), $"Content type '{contentType}' is not allowed. Use one of: {string.Join(", ", options.AllowedContentTypes)}.");

        // Buffered rather than streamed straight to storage: the checksum has to be computed before
        // we know whether to store the bytes at all, and 16 MB is small enough that holding it in
        // memory once is simpler than a two-pass streaming hash.
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(buffer, cancellationToken)).ToLowerInvariant();

        var existing = await _context.MediaAssets.FirstOrDefaultAsync(m => m.Checksum == checksum, cancellationToken);
        if (existing is not null)
            return ToDto(existing);

        buffer.Position = 0;
        var stored = await _storage.UploadAsync(buffer, fileName, contentType, cancellationToken);

        var asset = new MediaAsset
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StorageProvider = "Local",
            StorageKey = stored.StorageKey,
            Url = stored.Url,
            Checksum = checksum,
            UploadedBy = uploadedBy
        };

        _context.MediaAssets.Add(asset);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(asset);
    }

    public async Task<MediaAssetDto> AddFromUrlAsync(string url, Guid? uploadedBy, CancellationToken cancellationToken = default)
    {
        if (_urlFetcher is null)
            throw Invalid(nameof(url), "Adding a file by link is not available.");

        url = (url ?? string.Empty).Trim();
        if (url.Length is 0 or > 1000)
            throw Invalid(nameof(url), "Enter the link of the file (up to 1000 characters).");

        // Adding the same link twice returns the first entry.
        var already = await _context.MediaAssets.FirstOrDefaultAsync(m => m.Url == url, cancellationToken);
        if (already is not null)
            return ToDto(already);

        var options = await _tenantConfig.GetMediaOptionsAsync(cancellationToken);

        MediaUrlFetchResult fetched;
        try
        {
            fetched = await _urlFetcher.FetchAsync(url, options.MaxSizeBytes, cancellationToken);
        }
        catch (MediaUrlFetchException ex)
        {
            throw Invalid(nameof(url), ex.Message);
        }

        if (fetched.Content.Length == 0)
            throw Invalid(nameof(url), "The link returned an empty file.");

        var extension = Path.GetExtension(fetched.FileName);
        var contentType = options.AllowedContentTypes.Contains(fetched.ContentType, StringComparer.OrdinalIgnoreCase)
            ? fetched.ContentType
            : ContentTypeFromExtension(extension);

        // Some hosts answer images as application/octet-stream, so the extension is the fallback - never a free pass.
        if (contentType is null || !options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw Invalid(nameof(url), $"That link is not an allowed file type. Use one of: {string.Join(", ", options.AllowedContentTypes)}.");

        var fileName = string.IsNullOrWhiteSpace(fetched.FileName) ? "media" + ExtensionFor(contentType) : fetched.FileName;
        var checksum = Convert.ToHexString(SHA256.HashData(fetched.Content)).ToLowerInvariant();

        // Our own copy is kept only so a template's image can be handed to Meta when the template is created; the
        // address everyone sees and messages carry stays the tenant's own link.
        await using var buffer = new MemoryStream(fetched.Content);
        var stored = await _storage.UploadAsync(buffer, fileName, contentType, cancellationToken);

        var asset = new MediaAsset
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = fetched.Content.Length,
            StorageProvider = MediaAssetLinks.ExternalProvider,
            StorageKey = stored.StorageKey,
            Url = url,
            Checksum = checksum,
            UploadedBy = uploadedBy
        };

        _context.MediaAssets.Add(asset);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(asset);
    }

    private static string? ContentTypeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".3gp" => "video/3gpp",
        _ => null,
    };

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "video/3gpp" => ".3gp",
        _ => string.Empty,
    };

    public async Task DeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var asset = await FindOrThrowAsync(id, cancellationToken);

        // Applies even with force: force only detaches step attachments, and a template still shows this image.
        if (await _context.MessageTemplates.AnyAsync(t => t.HeaderMediaAssetId == id, cancellationToken))
            throw new ConflictException($"Media asset '{asset.FileName}' is the image of a message template. Change or remove it there before deleting the file.");

        if (!force)
        {
            var inUse = await _context.CampaignStepMedia.AnyAsync(m => m.MediaAssetId == id, cancellationToken);
            if (inUse)
                throw new ConflictException($"Media asset '{asset.FileName}' is attached to one or more campaign steps. Re-send with force=true to delete it anyway.");
        }
        else
        {
            var attachments = _context.CampaignStepMedia.Where(m => m.MediaAssetId == id);
            _context.CampaignStepMedia.RemoveRange(attachments);
        }

        await _storage.DeleteAsync(asset.StorageKey, cancellationToken);

        _context.MediaAssets.Remove(asset);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<MediaAsset> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.MediaAssets.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(MediaAsset), id);

    private MediaAssetDto ToDto(MediaAsset m)
    {
        var url = MediaAssetLinks.PublicUrl(m.StorageProvider, m.StorageKey, m.Url, _storage);
        return new(m.Id, m.FileName, m.ContentType, m.SizeBytes, url, m.CreatedAt, _storage.IsPublicUrl(url));
    }

    /// <summary>File-level checks (size/type) do not go through FluentValidation - there is no DTO
    /// to validate, just a stream - so this mirrors the same 400-mapped exception UserService already
    /// uses for Identity errors, rather than inventing a new exception type for one case.</summary>
    private static ValidationException Invalid(string property, string message) =>
        new(new[] { new ValidationFailure(property, message) });
}
