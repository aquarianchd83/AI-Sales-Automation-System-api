using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Media;

namespace WhatsAppSalesAutomation.Application.Media;

public class MediaService : IMediaService
{
    private readonly IApplicationDbContext _context;
    private readonly IMediaStorageService _storage;
    private readonly MediaOptions _options;

    public MediaService(IApplicationDbContext context, IMediaStorageService storage, IOptions<MediaOptions> options)
    {
        _context = context;
        _storage = storage;
        _options = options.Value;
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

        if (sizeBytes > _options.MaxSizeBytes)
            throw Invalid(nameof(sizeBytes), $"File exceeds the {_options.MaxSizeBytes / (1024 * 1024)} MB limit.");

        if (!_options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw Invalid(nameof(contentType), $"Content type '{contentType}' is not allowed. Use one of: {string.Join(", ", _options.AllowedContentTypes)}.");

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

    public async Task DeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        var asset = await FindOrThrowAsync(id, cancellationToken);

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

    private static MediaAssetDto ToDto(MediaAsset m) => new(m.Id, m.FileName, m.ContentType, m.SizeBytes, m.Url, m.CreatedAt);

    /// <summary>File-level checks (size/type) do not go through FluentValidation - there is no DTO
    /// to validate, just a stream - so this mirrors the same 400-mapped exception UserService already
    /// uses for Identity errors, rather than inventing a new exception type for one case.</summary>
    private static ValidationException Invalid(string property, string message) =>
        new(new[] { new ValidationFailure(property, message) });
}
