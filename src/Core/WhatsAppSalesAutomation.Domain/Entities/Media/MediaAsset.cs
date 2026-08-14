using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Media;

/// <summary>
/// A file in the Media Library (campaign images/videos). <see cref="StorageKey"/> is the storage
/// provider's own address for the bytes (a relative path today, an Azure Blob/S3 key later - the
/// same field either way, per <c>IMediaStorageService</c>'s abstraction).
/// </summary>
public class MediaAsset : BaseEntity
{
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string StorageProvider { get; set; } = string.Empty;

    public string StorageKey { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the file content, hex-encoded. Two uploads of the same bytes resolve to one
    /// MediaAsset instead of duplicate storage entries.
    /// </summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>
    /// Meta's media handle, populated the first time this asset is actually sent - uploading to
    /// WhatsApp is a separate step from uploading to our own storage, and the handle is cached here
    /// so a busy campaign does not re-upload the same file to Meta on every send.
    /// </summary>
    public string? WhatsAppMediaId { get; set; }

    public Guid? UploadedBy { get; set; }
}
