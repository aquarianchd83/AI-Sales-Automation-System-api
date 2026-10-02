using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>Stores media in the platform's S3 bucket. Storage keys carry the <see cref="KeyMarker"/> so the router can tell
/// them from local-disk keys; the object key (without the marker) is <c>{prefix}/{tenantId}/{yyyy}/{MM}/{guid}.ext</c>.</summary>
public class S3MediaStorageService : IMediaStorageService
{
    public const string KeyMarker = "s3:";

    private readonly S3MediaStorageSettings _settings;
    private readonly ITenantContext _tenant;
    private readonly Lazy<IAmazonS3> _client;

    public S3MediaStorageService(IOptions<S3MediaStorageSettings> settings, ITenantContext tenant)
    {
        _settings = settings.Value;
        _tenant = tenant;
        _client = new Lazy<IAmazonS3>(CreateClient);
    }

    public async Task<MediaStorageResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var now = DateTime.UtcNow;
        var tenantFolder = _tenant.TenantId?.ToString("N") ?? "shared";
        var objectKey = $"{Prefix}/{tenantFolder}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}";

        var request = new PutObjectRequest
        {
            BucketName = _settings.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
        };
        // The key is a generated name that never changes, so browsers and Meta can cache the file hard.
        request.Headers.CacheControl = "public, max-age=31536000";
        await _client.Value.PutObjectAsync(request, cancellationToken);

        var storageKey = KeyMarker + objectKey;
        return new MediaStorageResult(storageKey, GetPublicUrl(storageKey));
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _client.Value.DeleteObjectAsync(_settings.BucketName, ObjectKey(storageKey), cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            using var response = await _client.Value.GetObjectAsync(_settings.BucketName, ObjectKey(storageKey), cancellationToken);
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("The media file is no longer in the bucket.", storageKey);
        }
    }

    public string GetPublicUrl(string storageKey)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.PublicBaseUrl)
            ? $"https://{_settings.BucketName}.s3.{_settings.Region}.amazonaws.com"
            : _settings.PublicBaseUrl.Trim().TrimEnd('/');
        return $"{baseUrl}/{ObjectKey(storageKey)}";
    }

    // The bucket serves the file itself, so the portal previews from the same address.
    public string GetLocalPath(string storageKey) => GetPublicUrl(storageKey);

    public bool IsPublicUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.IsLoopback;

    private string Prefix => _settings.KeyPrefix.Trim().Trim('/');

    private static string ObjectKey(string storageKey) => storageKey.StartsWith(KeyMarker) ? storageKey[KeyMarker.Length..] : storageKey;

    private void EnsureConfigured()
    {
        if (!_settings.IsConfigured)
            throw new InvalidOperationException("Media storage is set to S3 but MediaStorage:S3:BucketName and Region are not configured. Ask the platform administrator to set them.");
    }

    private IAmazonS3 CreateClient()
    {
        var region = RegionEndpoint.GetBySystemName(_settings.Region);
        return string.IsNullOrWhiteSpace(_settings.AccessKeyId) || string.IsNullOrWhiteSpace(_settings.SecretAccessKey)
            ? new AmazonS3Client(region)
            : new AmazonS3Client(new BasicAWSCredentials(_settings.AccessKeyId, _settings.SecretAccessKey), region);
    }
}
