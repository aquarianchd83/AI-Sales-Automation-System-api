namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Stores the raw bytes of an uploaded media file. Implemented in Infrastructure - local disk today,
/// swappable for Azure Blob/S3 later without touching <c>MediaService</c>.
/// </summary>
public interface IMediaStorageService
{
    Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="Url"/> is where the file can be fetched from - relative for local disk, absolute
/// for a cloud provider - and is what gets uploaded to WhatsApp's media endpoint at send time.
/// </summary>
public record MediaStorageResult(string StorageKey, string Url);
