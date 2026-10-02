using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
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
        await Translate(() => _client.Value.PutObjectAsync(request, cancellationToken));

        var storageKey = KeyMarker + objectKey;
        return new MediaStorageResult(storageKey, GetPublicUrl(storageKey));
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await Translate(() => _client.Value.DeleteObjectAsync(_settings.BucketName, ObjectKey(storageKey), cancellationToken));
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            using var response = await Translate(() => _client.Value.GetObjectAsync(_settings.BucketName, ObjectKey(storageKey), cancellationToken));
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

    /// <summary>Turns AWS's low-level failures into a sentence the platform admin can act on; the original is kept as the inner exception for the log.</summary>
    private async Task<T> Translate<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && ex.ErrorCode == "NoSuchKey")
        {
            throw;
        }
        catch (AmazonS3Exception ex)
        {
            var hint = ex.ErrorCode switch
            {
                "NoSuchBucket" => $"bucket '{_settings.BucketName}' does not exist in {_settings.Region}",
                "InvalidAccessKeyId" or "SignatureDoesNotMatch" => "the access key or secret is wrong",
                "AccessDenied" or "AllAccessDisabled" => "the AWS identity is not allowed to write to the bucket (it needs s3:PutObject, s3:GetObject and s3:DeleteObject)",
                "PermanentRedirect" or "AuthorizationHeaderMalformed" => $"the bucket is not in region {_settings.Region}",
                _ => ex.Message,
            };
            throw new StorageUnavailableException($"Media storage (S3) failed: {hint}. Ask the platform administrator to check MediaStorage:S3.", ex);
        }
        catch (AmazonClientException ex)
        {
            var hint = _settings.HasAccessKeys
                ? ex.Message
                : "no AWS credentials were found - fill in MediaStorage:S3:AccessKeyId and SecretAccessKey (this server has no AWS role or default profile)";
            throw new StorageUnavailableException($"Media storage (S3) failed: {hint}.", ex);
        }
    }

    private Task Translate(Func<Task> call) => Translate(async () =>
    {
        await call();
        return true;
    });

    private void EnsureConfigured()
    {
        if (!_settings.IsConfigured)
            throw new StorageUnavailableException("Media storage is set to S3 but MediaStorage:S3:BucketName and Region are not configured. Ask the platform administrator to set them.");
    }

    private IAmazonS3 CreateClient()
    {
        var region = RegionEndpoint.GetBySystemName(_settings.Region);
        return !_settings.HasAccessKeys
            ? new AmazonS3Client(region)
            : new AmazonS3Client(new BasicAWSCredentials(_settings.AccessKeyId, _settings.SecretAccessKey), region);
    }
}
