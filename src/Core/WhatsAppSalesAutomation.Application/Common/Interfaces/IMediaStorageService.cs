namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Stores the raw bytes of an uploaded media file. Implemented in Infrastructure - local disk today,
/// swappable for Azure Blob/S3 later without touching <c>MediaService</c>.
/// </summary>
public interface IMediaStorageService
{
    Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading (e.g. to hand its bytes to Meta). The caller disposes the stream.
    /// Throws <see cref="FileNotFoundException"/> if the file is gone.</summary>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>The link a stored file is reachable at right now, built from the CURRENT configuration (for local
    /// disk: MediaStorage PublicBaseUrl + PublicBasePath). Recomputed rather than read from the stored
    /// <c>MediaAsset.Url</c>, which was frozen at upload time and goes stale when the base URL is set or changes.</summary>
    string GetPublicUrl(string storageKey);

    /// <summary>True when <paramref name="url"/> is something Meta's servers can fetch: absolute http(s) on a host
    /// that is not this machine. False means the storage's public base URL is not configured (or points at localhost).</summary>
    bool IsPublicUrl(string url);
}

/// <summary>
/// <paramref name="Url"/> is where the file can be fetched from - relative for local disk, absolute
/// for a cloud provider - and is what gets uploaded to WhatsApp's media endpoint at send time.
/// </summary>
public record MediaStorageResult(string StorageKey, string Url);
