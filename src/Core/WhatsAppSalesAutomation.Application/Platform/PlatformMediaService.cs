using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Media;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The platform's own media library - the images the WhatsApp notice templates show. Separate from every tenant's library: its
/// files are the operator's, are listed only here, and cost no tenant any storage.</summary>
public interface IPlatformMediaService
{
    Task<IReadOnlyList<MediaAssetDto>> GetAllAsync(string? search, CancellationToken cancellationToken = default);

    Task<MediaAssetDto> UploadAsync(Stream content, string fileName, string contentType, long sizeBytes, Guid? uploadedBy, CancellationToken cancellationToken = default);

    /// <summary>Swaps the file of an entry, keeping its id so every template using it shows the new one.</summary>
    Task<MediaAssetDto> ReplaceAsync(Guid id, Stream content, string fileName, string contentType, long sizeBytes, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public class PlatformMediaService : IPlatformMediaService
{
    private readonly IApplicationDbContext _context;
    private readonly IMediaStorageService _storage;
    private readonly IOptionsSnapshot<MediaOptions> _options;

    public PlatformMediaService(IApplicationDbContext context, IMediaStorageService storage, IOptionsSnapshot<MediaOptions> options)
    {
        _context = context;
        _storage = storage;
        _options = options;
    }

    public async Task<IReadOnlyList<MediaAssetDto>> GetAllAsync(string? search, CancellationToken cancellationToken = default)
    {
        var query = _context.PlatformMediaAssets.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(m => m.FileName.Contains(term));
        }

        var items = await query.OrderByDescending(m => m.CreatedAt).ToListAsync(cancellationToken);
        return items.Select(ToDto).ToList();
    }

    public async Task<MediaAssetDto> UploadAsync(
        Stream content, string fileName, string contentType, long sizeBytes, Guid? uploadedBy, CancellationToken cancellationToken = default)
    {
        CheckFile(sizeBytes, contentType);

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(buffer, cancellationToken)).ToLowerInvariant();

        var existing = await _context.PlatformMediaAssets.FirstOrDefaultAsync(m => m.Checksum == checksum, cancellationToken);
        if (existing is not null)
            return ToDto(existing);

        buffer.Position = 0;
        var stored = await _storage.UploadAsync(buffer, fileName, contentType, cancellationToken);

        var asset = new PlatformMediaAsset
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StorageProvider = stored.StorageKey.StartsWith("s3:") ? "S3" : "Local",
            StorageKey = stored.StorageKey,
            Url = stored.Url,
            Checksum = checksum,
            UploadedBy = uploadedBy
        };

        _context.PlatformMediaAssets.Add(asset);
        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(asset);
    }

    public async Task<MediaAssetDto> ReplaceAsync(
        Guid id, Stream content, string fileName, string contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        var asset = await FindOrThrowAsync(id, cancellationToken);
        CheckFile(sizeBytes, contentType);

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(buffer, cancellationToken)).ToLowerInvariant();

        buffer.Position = 0;
        var stored = await _storage.UploadAsync(buffer, fileName, contentType, cancellationToken);

        var oldKey = asset.StorageKey;
        asset.FileName = fileName;
        asset.ContentType = contentType;
        asset.SizeBytes = sizeBytes;
        asset.StorageProvider = stored.StorageKey.StartsWith("s3:") ? "S3" : "Local";
        asset.StorageKey = stored.StorageKey;
        asset.Url = stored.Url;
        asset.Checksum = checksum;
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            await _storage.DeleteAsync(oldKey, cancellationToken);
        }
        catch (Exception)
        {
            // An orphaned old file is harmless; failing the replace after it succeeded would not be.
        }

        return ToDto(asset);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var asset = await FindOrThrowAsync(id, cancellationToken);

        var user = await _context.PlatformMessageTemplates.Where(t => t.HeaderMediaAssetId == id).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken);
        if (user is not null)
            throw new ConflictException($"'{asset.FileName}' is the image of the template \"{user}\". Change or remove it there before deleting the file.");

        await _storage.DeleteAsync(asset.StorageKey, cancellationToken);
        _context.PlatformMediaAssets.Remove(asset);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private void CheckFile(long sizeBytes, string contentType)
    {
        var options = _options.Value;
        if (sizeBytes <= 0)
            throw Invalid("file", "The uploaded file is empty.");
        if (options.MaxSizeBytes > 0 && sizeBytes > options.MaxSizeBytes)
            throw Invalid("file", $"File exceeds the {options.MaxSizeBytes / (1024 * 1024)} MB limit.");
        if (!options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw Invalid("file", $"Content type '{contentType}' is not allowed. Use one of: {string.Join(", ", options.AllowedContentTypes)}.");
    }

    private async Task<PlatformMediaAsset> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.PlatformMediaAssets.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlatformMediaAsset), id);

    private MediaAssetDto ToDto(PlatformMediaAsset m)
    {
        var url = MediaAssetLinks.PublicUrl(m.StorageProvider, m.StorageKey, m.Url, _storage);
        return new MediaAssetDto(m.Id, m.FileName, m.ContentType, m.SizeBytes, url, m.CreatedAt, _storage.IsPublicUrl(url),
            MediaAssetLinks.PreviewUrl(m.StorageProvider, m.StorageKey, m.Url, _storage));
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
