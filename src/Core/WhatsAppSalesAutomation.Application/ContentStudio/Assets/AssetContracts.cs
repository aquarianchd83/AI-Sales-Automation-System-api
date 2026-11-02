using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Assets;

/// <summary>The platform's key for stock photo search (Platform &gt; Configuration &gt; ContentStudio). Empty means stock search is not set up.</summary>
public class StockOptions
{
    public string PexelsApiKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PexelsApiKey);
}

/// <summary>A photo as a stock library describes it. Everything here comes from the library itself, never from the browser.</summary>
public sealed record StockPhoto(
    string ProviderAssetId, string Provider, string PreviewUrl, string DownloadUrl, string PageUrl, string Photographer, string? PhotographerUrl,
    int Width, int Height, string? Description, string LicenseName, string LicenseUrl)
{
    public string Attribution => $"Photo by {Photographer} on {Provider}";
}

/// <param name="Message">Why there are no photos when the search could not run (not set up, rate-limited, provider error); null otherwise.</param>
public sealed record StockSearchResult(IReadOnlyList<StockPhoto> Photos, string? Message);

public sealed record StockImage(byte[] Content);

/// <summary>A stock photo library. Provider-agnostic so a second library can sit behind it; today only Pexels is implemented.</summary>
public interface IStockAssetProvider
{
    string Name { get; }

    /// <summary>False when the platform has no key for it.</summary>
    bool IsConfigured { get; }

    Task<StockSearchResult> SearchAsync(string query, int count, CancellationToken cancellationToken = default);

    /// <summary>The library's own record of one photo - the authority when a tenant chooses it. Null when it no longer exists.</summary>
    Task<StockPhoto?> GetAsync(string providerAssetId, CancellationToken cancellationToken = default);

    /// <summary>Fetches the picture itself, from the library's own image host only, and no more than <paramref name="maxBytes"/> of it.</summary>
    Task<StockImage> DownloadAsync(StockPhoto photo, int maxBytes, CancellationToken cancellationToken = default);
}

// ----- requests --------------------------------------------------------------------------------------------------------------------

/// <param name="Kind">Upload or Screenshot. A stock photo is chosen from a search, never uploaded.</param>
/// <param name="RightsConfirmed">The tenant confirms they may use the picture and that anyone identifiable in it has agreed.</param>
/// <param name="PersonalDataChecked">For a screenshot: the tenant confirms it shows no patient or customer personal data.</param>
public sealed record UploadAssetRequest(
    Stream Content, long Length, string FileName, StudioAssetKind Kind, string? Name, string? Description, string? Tags, bool RightsConfirmed, bool PersonalDataChecked);

/// <summary>Either one of the tenant's own assets (<see cref="AssetId"/>) or a stock photo found by search (<see cref="StockProviderAssetId"/>).</summary>
public sealed record ChooseAssetRequest(Guid? AssetId, string? StockProviderAssetId);

// ----- results ---------------------------------------------------------------------------------------------------------------------

public sealed record AssetDto(
    Guid Id, string Kind, string KindLabel, string Name, string? Description, IReadOnlyList<string> Tags, string ContentType, long SizeBytes, int? Width, int? Height,
    string? Provider, string LicenseName, string? LicenseUrl, string? Attribution, string? SourceUrl, bool RightsConfirmed, bool PersonalDataChecked, int UsedInScenes, DateTime CreatedAt);

/// <summary>The part of an asset a scene shows: what it is, whose it is, and under what terms.</summary>
public sealed record AssetSummaryDto(Guid Id, string Kind, string KindLabel, string Name, string LicenseName, string? Attribution, int? Width, int? Height);

public sealed record AssetCandidateDto(AssetDto Asset, double Score, string Reason);

public sealed record StockPhotoDto(
    string ProviderAssetId, string Provider, string PreviewUrl, string PageUrl, string Photographer, int Width, int Height, string? Description,
    string LicenseName, string LicenseUrl, string Attribution);

/// <param name="Query">The words used to look (the scene's picture note unless the tenant typed their own).</param>
/// <param name="StockAvailable">False when stock search is not set up on the platform; <see cref="StockMessage"/> then says so.</param>
public sealed record AssetCandidatesDto(string Query, IReadOnlyList<AssetCandidateDto> Own, IReadOnlyList<StockPhotoDto> Stock, bool StockAvailable, string? StockMessage);

public sealed record AssetFile(Stream Content, string ContentType, string FileName);

public interface IStudioAssetService
{
    Task<IReadOnlyList<AssetDto>> ListAsync(Guid tenantId, string? search, CancellationToken cancellationToken = default);

    Task<AssetDto> UploadAsync(Guid tenantId, UploadAssetRequest request, CancellationToken cancellationToken = default);

    Task<AssetFile> OpenAsync(Guid tenantId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>Removes a picture the tenant no longer wants. Refused while a scene uses it.</summary>
    Task DeleteAsync(Guid tenantId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>Ranked pictures for a scene: the tenant's own library first, and (when asked) stock photos matching the scene's picture note or the tenant's own words.</summary>
    Task<AssetCandidatesDto> CandidatesAsync(Guid tenantId, Guid sceneId, bool includeStock, string? query, CancellationToken cancellationToken = default);

    /// <summary>Sets the picture of a scene. A stock photo is copied to the tenant's folder first, with its licence and credit.</summary>
    Task<AssetSummaryDto> ChooseAsync(Guid tenantId, Guid sceneId, ChooseAssetRequest request, CancellationToken cancellationToken = default);

    Task ClearAsync(Guid tenantId, Guid sceneId, CancellationToken cancellationToken = default);
}
