using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A file in the platform's own media library (images for the notice templates). The same shape as a tenant's
/// <c>MediaAsset</c>, but platform-global: tenants never see or list it, and it is not counted against anyone's storage.
/// </summary>
public class PlatformMediaAsset : BaseEntity
{
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string StorageProvider { get; set; } = string.Empty;

    public string StorageKey { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>SHA-256 of the content, hex - an upload of bytes already stored resolves to the existing entry.</summary>
    public string Checksum { get; set; } = string.Empty;

    public Guid? UploadedBy { get; set; }
}
