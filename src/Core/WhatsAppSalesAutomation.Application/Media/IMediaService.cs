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

    /// <summary>Adds a file the tenant already hosts: downloads it once (to check type and size, and to hand to Meta when a
    /// template is created) but keeps THEIR link as the public address, instead of one of ours.</summary>
    Task<MediaAssetDto> AddFromUrlAsync(string url, Guid? uploadedBy, CancellationToken cancellationToken = default);

    /// <summary>Refused (409) if the asset is still attached to a campaign step, unless <paramref name="force"/> is set.</summary>
    Task DeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default);
}
