using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Assets;

/// <summary>
/// The pictures a tenant may put in a video. Uploads and screenshots are the tenant's own, accepted only with their confirmation that they may
/// use them (and, for a screenshot, that it shows no patient or customer data); a stock photo is copied into the tenant's folder when chosen,
/// with its licence and credit taken from the library itself rather than from the browser. Every file is checked by its first bytes, kept once
/// per tenant, stored privately and read back only through the API. A picture can be set on a scene only while the script is a draft.
/// </summary>
public sealed class StudioAssetService : IStudioAssetService
{
    public const int MaxImageBytes = 10 * 1024 * 1024;
    public const int StockResults = 12;
    private const int OwnCandidates = 24;

    private readonly IApplicationDbContext _db;
    private readonly ITenantDocumentStore _store;
    private readonly IStockAssetProvider _stock;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<StudioAssetService> _logger;

    public StudioAssetService(
        IApplicationDbContext db, ITenantDocumentStore store, IStockAssetProvider stock, ICurrentUserService user, IDateTimeProvider clock, ILogger<StudioAssetService> logger)
    {
        _db = db;
        _store = store;
        _stock = stock;
        _user = user;
        _clock = clock;
        _logger = logger;
    }

    // ----- the library -----------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AssetDto>> ListAsync(Guid tenantId, string? search, CancellationToken ct = default)
    {
        var assets = await _db.StudioAssets.AsNoTracking().Where(a => a.TenantId == tenantId).OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
        var tokens = AssetMatcher.Tokens(search);
        if (tokens.Count > 0)
            assets = assets.Where(a => AssetMatcher.Score(tokens, a).Score > 0).ToList();
        var usage = await UsageAsync(tenantId, ct);
        return assets.Select(a => ToDto(a, usage.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task<AssetDto> UploadAsync(Guid tenantId, UploadAssetRequest request, CancellationToken ct = default)
    {
        if (request.Kind is not (StudioAssetKind.Upload or StudioAssetKind.Screenshot))
            throw Invalid("kind", "A stock photo is chosen from a search, not uploaded.");
        if (!request.RightsConfirmed)
            throw Invalid("rightsConfirmed", "Confirm that you may use this picture and that anyone you can recognise in it has agreed.");
        if (request.Kind == StudioAssetKind.Screenshot && !request.PersonalDataChecked)
            throw Invalid("personalDataChecked", "Confirm that the screenshot shows no patient or customer personal data.");
        if (request.Length <= 0)
            throw Invalid("file", "Choose a picture to upload.");
        if (request.Length > MaxImageBytes)
            throw Invalid("file", $"The picture is larger than {MaxImageBytes / (1024 * 1024)} MB.");
        EnsureStorage();

        var bytes = await ReadAsync(request.Content, ct);
        var image = ImageSniffer.Detect(bytes) ?? throw Invalid("file", "Only JPEG, PNG and WebP pictures can be used.");
        var checksum = ImageSniffer.Checksum(bytes);

        var existing = await _db.StudioAssets.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Checksum == checksum, ct);
        if (existing is not null)
            return ToDto(existing, (await UsageAsync(tenantId, ct)).GetValueOrDefault(existing.Id)); // already in the library: not stored twice

        var asset = new StudioAsset
        {
            TenantId = tenantId, Kind = request.Kind, Name = Clean(request.Name, 200) ?? AssetMatcher.NameFromFile(request.FileName), Description = Clean(request.Description, 500),
            Tags = AssetMatcher.NormaliseTags(request.Tags), ContentType = image.ContentType, SizeBytes = bytes.Length, Width = image.Width, Height = image.Height, Checksum = checksum,
            LicenseName = "Provided by you", RightsConfirmed = true, PersonalDataChecked = request.Kind == StudioAssetKind.Screenshot, CreatedBy = _user.UserId
        };
        asset.StorageKey = await SaveAsync(tenantId, bytes, image, ct);
        _db.StudioAssets.Add(asset);
        await SaveOrCleanUpAsync(asset, ct);
        return ToDto(asset, 0);
    }

    public async Task<AssetFile> OpenAsync(Guid tenantId, Guid assetId, CancellationToken ct = default)
    {
        var asset = await FindAssetAsync(tenantId, assetId, ct);
        // Defence in depth: the key must sit in this tenant's own folder, whatever the row says.
        if (!asset.StorageKey.Contains(_store.FolderOf(tenantId), StringComparison.Ordinal))
            throw new NotFoundException("StudioAsset", assetId);
        var extension = asset.ContentType switch { "image/png" => "png", "image/webp" => "webp", _ => "jpg" };
        return new AssetFile(await _store.OpenReadAsync(asset.StorageKey, ct), asset.ContentType, $"{asset.Name}.{extension}");
    }

    public async Task DeleteAsync(Guid tenantId, Guid assetId, CancellationToken ct = default)
    {
        var asset = await FindAssetAsync(tenantId, assetId, ct);
        var used = await _db.ContentScenes.CountAsync(s => s.TenantId == tenantId && s.ChosenAssetId == assetId, ct);
        if (used > 0)
            throw new ConflictException($"This picture is used in {used} scene{(used == 1 ? string.Empty : "s")}. Take it out of {(used == 1 ? "that scene" : "those scenes")} first.");

        _db.StudioAssets.Remove(asset);
        await _db.SaveChangesAsync(ct);
        try
        {
            await _store.DeleteAsync(asset.StorageKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The file of studio asset {AssetId} could not be removed from storage.", assetId);
        }
    }

    // ----- choosing for a scene --------------------------------------------------------------------------------------------------

    public async Task<AssetCandidatesDto> CandidatesAsync(Guid tenantId, Guid sceneId, bool includeStock, string? query, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(tenantId, sceneId, ct, track: false);
        var words = Clean(query, 120) ?? AssetMatcher.QueryFor(scene);
        var tokens = AssetMatcher.Tokens(words);

        var assets = await _db.StudioAssets.AsNoTracking().Where(a => a.TenantId == tenantId).OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(ct);
        var usage = await UsageAsync(tenantId, ct);
        var own = assets
            .Select(a => (Asset: a, Match: AssetMatcher.Score(tokens, a)))
            .OrderByDescending(x => x.Match.Score).ThenByDescending(x => x.Asset.CreatedAt)
            .Take(OwnCandidates)
            .Select(x => new AssetCandidateDto(ToDto(x.Asset, usage.GetValueOrDefault(x.Asset.Id)), x.Match.Score, x.Match.Reason))
            .ToList();

        if (!includeStock)
            return new AssetCandidatesDto(words, own, Array.Empty<StockPhotoDto>(), _stock.IsConfigured, null);
        if (!_stock.IsConfigured)
            return new AssetCandidatesDto(words, own, Array.Empty<StockPhotoDto>(), false, "Stock photo search is not set up on this platform yet. Your own pictures can still be used.");
        if (string.IsNullOrWhiteSpace(words))
            return new AssetCandidatesDto(words, own, Array.Empty<StockPhotoDto>(), true, "Describe the picture you need, then search.");

        var found = await _stock.SearchAsync(words, StockResults, ct);
        var photos = found.Photos.Select(p => new StockPhotoDto(
            p.ProviderAssetId, p.Provider, p.PreviewUrl, p.PageUrl, p.Photographer, p.Width, p.Height, p.Description, p.LicenseName, p.LicenseUrl, p.Attribution)).ToList();
        return new AssetCandidatesDto(words, own, photos, true, found.Message ?? (photos.Count == 0 ? "No stock photos matched. Try other words." : null));
    }

    public async Task<AssetSummaryDto> ChooseAsync(Guid tenantId, Guid sceneId, ChooseAssetRequest request, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(tenantId, sceneId, ct);
        await EnsureDraftAsync(tenantId, scene.VersionId, ct);

        StudioAsset asset;
        if (request?.AssetId is { } assetId)
            asset = await FindAssetAsync(tenantId, assetId, ct, track: false);
        else if (!string.IsNullOrWhiteSpace(request?.StockProviderAssetId))
            asset = await CopyStockAsync(tenantId, request.StockProviderAssetId.Trim(), ct);
        else
            throw Invalid("assetId", "Choose one of your pictures or a stock photo.");

        scene.ChosenAssetId = asset.Id;
        scene.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToSummary(asset);
    }

    public async Task ClearAsync(Guid tenantId, Guid sceneId, CancellationToken ct = default)
    {
        var scene = await FindSceneAsync(tenantId, sceneId, ct);
        await EnsureDraftAsync(tenantId, scene.VersionId, ct);
        scene.ChosenAssetId = null;
        scene.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Copies a stock photo into the tenant's folder (once), taking its licence and credit from the library's own record.</summary>
    private async Task<StudioAsset> CopyStockAsync(Guid tenantId, string providerAssetId, CancellationToken ct)
    {
        if (!_stock.IsConfigured)
            throw new ConflictException("Stock photo search is not set up on this platform yet.");

        var already = await _db.StudioAssets.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Provider == _stock.Name && a.ProviderAssetId == providerAssetId, ct);
        if (already is not null)
            return already;

        EnsureStorage();
        var photo = await _stock.GetAsync(providerAssetId, ct) ?? throw new ConflictException("That stock photo is no longer available. Search again.");
        StockImage download;
        try
        {
            download = await _stock.DownloadAsync(photo, MaxImageBytes, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not copy stock photo {PhotoId} for tenant {TenantId}.", providerAssetId, tenantId);
            throw new ConflictException("The stock photo could not be downloaded right now. Try again in a moment.");
        }

        var image = ImageSniffer.Detect(download.Content) ?? throw new ConflictException("The stock library returned something that is not a picture.");
        var checksum = ImageSniffer.Checksum(download.Content);
        var sameFile = await _db.StudioAssets.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Checksum == checksum, ct);
        if (sameFile is not null)
            return sameFile;

        var asset = new StudioAsset
        {
            TenantId = tenantId, Kind = StudioAssetKind.Stock, Name = Clean(photo.Description, 200) ?? $"{photo.Provider} photo {photo.ProviderAssetId}", Description = Clean(photo.Description, 500),
            ContentType = image.ContentType, SizeBytes = download.Content.Length, Width = image.Width ?? photo.Width, Height = image.Height ?? photo.Height, Checksum = checksum,
            Provider = photo.Provider, ProviderAssetId = photo.ProviderAssetId, SourceUrl = Clean(photo.PageUrl, 500), LicenseName = photo.LicenseName, LicenseUrl = Clean(photo.LicenseUrl, 300),
            Attribution = Clean(photo.Attribution, 300), CreatedBy = _user.UserId
        };
        asset.StorageKey = await SaveAsync(tenantId, download.Content, image, ct);
        _db.StudioAssets.Add(asset);
        await SaveOrCleanUpAsync(asset, ct);
        return asset;
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private void EnsureStorage()
    {
        if (!_store.IsConfigured)
            throw new ConflictException("File storage (AWS S3) is not set up yet, so pictures cannot be kept. Ask the platform administrator to check the AWS Settings.");
    }

    private async Task<string> SaveAsync(Guid tenantId, byte[] content, SniffedImage image, CancellationToken ct)
    {
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await using var stream = new MemoryStream(content);
        return await _store.SaveAsync(tenantId, $"studio/assets/{_clock.UtcNow:yyyyMM}/{random}.{image.Extension}", stream, image.ContentType, ct);
    }

    /// <summary>Saves the row; if it fails (for example a second upload of the same file won the race) the file just written is removed again.</summary>
    private async Task SaveOrCleanUpAsync(StudioAsset asset, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try
            {
                await _store.DeleteAsync(asset.StorageKey, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Orphaned studio file {Key} could not be removed.", asset.StorageKey);
            }

            throw;
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes)
                throw Invalid("file", $"The picture is larger than {MaxImageBytes / (1024 * 1024)} MB."); // the declared length cannot be trusted
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<Dictionary<Guid, int>> UsageAsync(Guid tenantId, CancellationToken ct) =>
        (await _db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && s.ChosenAssetId != null).Select(s => s.ChosenAssetId!.Value).ToListAsync(ct))
        .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    private async Task<StudioAsset> FindAssetAsync(Guid tenantId, Guid assetId, CancellationToken ct, bool track = true)
    {
        var query = track ? _db.StudioAssets.AsQueryable() : _db.StudioAssets.AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == tenantId, ct) ?? throw new NotFoundException("StudioAsset", assetId);
    }

    private async Task<ContentScene> FindSceneAsync(Guid tenantId, Guid sceneId, CancellationToken ct, bool track = true)
    {
        var query = track ? _db.ContentScenes.AsQueryable() : _db.ContentScenes.AsNoTracking();
        var scene = await query.FirstOrDefaultAsync(s => s.Id == sceneId && s.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentScene", sceneId);
        var version = await _db.ContentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == scene.VersionId && v.TenantId == tenantId, ct);
        if (version is null || !await _db.ContentProjects.AnyAsync(p => p.Id == version.ProjectId && p.TenantId == tenantId && !p.IsDeleted, ct))
            throw new NotFoundException("ContentScene", sceneId);
        return scene;
    }

    /// <summary>A picture can be changed only while the script is a draft: what is in review or approved is what was looked at.</summary>
    private async Task EnsureDraftAsync(Guid tenantId, Guid versionId, CancellationToken ct)
    {
        var status = await _db.ContentVersions.AsNoTracking().Where(v => v.Id == versionId && v.TenantId == tenantId).Select(v => v.Status).FirstAsync(ct);
        if (status != ContentVersionStatus.Draft)
            throw new ConflictException("This script is no longer a draft, so its pictures cannot be changed. Write a new version instead.");
    }

    public static string KindLabel(StudioAsset asset) => asset.Kind switch
    {
        StudioAssetKind.Upload => "Your upload",
        StudioAssetKind.Screenshot => "Your screenshot",
        _ => $"Stock photo ({asset.Provider})",
    };

    public static AssetSummaryDto ToSummary(StudioAsset a) => new(a.Id, a.Kind.ToString(), KindLabel(a), a.Name, a.LicenseName, a.Attribution, a.Width, a.Height);

    private static AssetDto ToDto(StudioAsset a, int usedIn) => new(
        a.Id, a.Kind.ToString(), KindLabel(a), a.Name, a.Description, (a.Tags ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries), a.ContentType, a.SizeBytes, a.Width, a.Height,
        a.Provider, a.LicenseName, a.LicenseUrl, a.Attribution, a.SourceUrl, a.RightsConfirmed, a.PersonalDataChecked, usedIn, a.CreatedAt);

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
