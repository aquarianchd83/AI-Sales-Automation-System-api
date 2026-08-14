using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Media;

public interface IMediaService
{
    Task<PagedResult<MediaAssetDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<MediaAssetDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads to storage and records a MediaAsset - or, if the content's checksum matches an
    /// existing asset, returns that one unchanged rather than storing a duplicate.
    /// </summary>
    Task<MediaAssetDto> UploadAsync(Stream content, string fileName, string contentType, long sizeBytes, Guid? uploadedBy, CancellationToken cancellationToken = default);

    /// <summary>Refused (409) if the asset is still attached to a campaign step, unless <paramref name="force"/> is set.</summary>
    Task DeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default);
}
