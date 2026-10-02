using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>The one <see cref="IMediaStorageService"/> the app sees. New uploads go where MediaStorage:Provider says (re-read every request, so the Platform Admin Console's AWS Settings apply with no restart);
/// every other call follows the file's own key, so switching the platform to S3 never strands files already on disk.</summary>
public class RoutingMediaStorageService : IMediaStorageService
{
    private readonly LocalFileMediaStorageService _local;
    private readonly S3MediaStorageService _s3;
    private readonly LocalMediaStorageSettings _settings;

    public RoutingMediaStorageService(LocalFileMediaStorageService local, S3MediaStorageService s3, IOptionsSnapshot<LocalMediaStorageSettings> settings)
    {
        _local = local;
        _s3 = s3;
        _settings = settings.Value;
    }

    private bool UseS3ForNewFiles => string.Equals(_settings.Provider, "S3", StringComparison.OrdinalIgnoreCase);

    private IMediaStorageService For(string storageKey) =>
        storageKey.StartsWith(S3MediaStorageService.KeyMarker) ? _s3 : _local;

    public Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default) =>
        (UseS3ForNewFiles ? (IMediaStorageService)_s3 : _local).UploadAsync(content, fileName, contentType, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => For(storageKey).DeleteAsync(storageKey, cancellationToken);

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => For(storageKey).OpenReadAsync(storageKey, cancellationToken);

    public string GetPublicUrl(string storageKey) => For(storageKey).GetPublicUrl(storageKey);

    public string GetLocalPath(string storageKey) => For(storageKey).GetLocalPath(storageKey);

    public bool IsPublicUrl(string url) => _local.IsPublicUrl(url);
}
