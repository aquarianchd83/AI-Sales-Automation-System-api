using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Infrastructure.Storage;

namespace WhatsAppSalesAutomation.Infrastructure.ProductKnowledge;

/// <summary>
/// The tenant's product-knowledge files (uploaded documents, the extracted text, and later the knowledge files) in the platform's AWS bucket,
/// under the tenant's own folder: <c>private/{tenantId}/product-knowledge/...</c>.
///
/// Deliberately NOT under the media prefix (<see cref="S3MediaStorageSettings.KeyPrefix"/>, "media" by default): that prefix is made publicly
/// readable by the bucket policy so WhatsApp can download template media by link. A tenant's price list or manual must not be readable by
/// anyone who guesses a key, so these objects live outside it, are encrypted at rest, are never cached publicly, and are read back only
/// through the tenant's own API. Uses the same bucket, region and credentials as the AWS Settings page.
/// </summary>
public sealed class S3TenantDocumentStore : ITenantDocumentStore
{
    public const string KeyMarker = "s3:";
    public const string Root = "private";

    private readonly S3MediaStorageSettings _settings;
    private readonly Lazy<IAmazonS3> _client;

    public S3TenantDocumentStore(IOptionsSnapshot<S3MediaStorageSettings> settings)
    {
        _settings = settings.Value;
        _client = new Lazy<IAmazonS3>(CreateClient);
    }

    public bool IsConfigured => _settings.IsConfigured;

    public string FolderOf(Guid tenantId) => $"{Root}/{tenantId:N}/product-knowledge/";

    public async Task<string> SaveAsync(Guid tenantId, string relativePath, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var objectKey = FolderOf(tenantId) + Clean(relativePath);

        var request = new PutObjectRequest
        {
            BucketName = _settings.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        };
        request.Headers.CacheControl = "private, no-store";

        await Translate(() => _client.Value.PutObjectAsync(request, cancellationToken));
        return KeyMarker + objectKey;
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
            throw new FileNotFoundException("The file is no longer in the bucket.", storageKey);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await Translate(() => _client.Value.DeleteObjectAsync(_settings.BucketName, ObjectKey(storageKey), cancellationToken));
    }

    private static string ObjectKey(string storageKey) => storageKey.StartsWith(KeyMarker) ? storageKey[KeyMarker.Length..] : storageKey;

    /// <summary>Only plain path segments: no "..", no leading slash, nothing that could step out of the tenant's folder.</summary>
    internal static string Clean(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .Select(s => new string(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()))
            .Where(s => s.Length > 0 && s.Trim('.').Length > 0);
        var path = string.Join('/', segments);
        if (path.Length == 0)
            throw new ArgumentException("A file path inside the tenant's folder is required.", nameof(relativePath));
        return path;
    }

    private void EnsureConfigured()
    {
        if (!_settings.IsConfigured)
            throw new StorageUnavailableException(
                "Documents are kept in the platform's AWS storage, which is not set up yet. Ask the platform administrator to fill in the AWS Settings.");
    }

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
                "AccessDenied" or "AllAccessDisabled" => $"the AWS identity is not allowed to use '{Root}/*' in the bucket (it needs s3:PutObject, s3:GetObject and s3:DeleteObject there)",
                _ => ex.Message,
            };
            throw new StorageUnavailableException($"Document storage (AWS S3) failed: {hint}. Ask the platform administrator to check the AWS Settings.", ex);
        }
        catch (AmazonClientException ex)
        {
            throw new StorageUnavailableException($"Document storage (AWS S3) failed: {(_settings.HasAccessKeys ? ex.Message : "no AWS credentials are set in the AWS Settings")}.", ex);
        }
    }

    private Task Translate(Func<Task> call) => Translate(async () =>
    {
        await call();
        return true;
    });

    private IAmazonS3 CreateClient()
    {
        var region = RegionEndpoint.GetBySystemName(_settings.Region);
        return !_settings.HasAccessKeys
            ? new AmazonS3Client(region)
            : new AmazonS3Client(new BasicAWSCredentials(_settings.AccessKeyId, _settings.SecretAccessKey), region);
    }
}

/// <summary>Bound from "ProductKnowledge" (the AppSettings rows the platform admin edits).</summary>
public sealed class ProductKnowledgeSettings
{
    public int MaxFileMb { get; set; } = 10;
    public int MaxSourcesPerTenant { get; set; } = 50;
    public int MaxScannedPages { get; set; } = 20;
    public int CrawlMaxPages { get; set; } = 50;
    public int CrawlMaxDepth { get; set; } = 2;
    public int CrawlPageTimeoutSeconds { get; set; } = 15;
    public int CrawlMaxPageKb { get; set; } = 2048;
    public bool CrawlRespectRobots { get; set; } = true;
    public int DraftPassages { get; set; } = 6;
    public int MaxCustomQuestions { get; set; } = 20;
}

/// <summary>The two On/Off switches on the Integrations page, bound from "Integrations:ProductSources". Absent means On.</summary>
public sealed class ProductSourceSwitches
{
    public bool DocumentUpload { get; set; } = true;
    public bool WebsiteScanning { get; set; } = true;
}

/// <summary>Reads the limits and switches per request (so a save applies at once), the limits clamped to sane ranges so a typo in the
/// settings cannot open the gates.</summary>
public sealed class SourceLimitsProvider : ISourceLimitsProvider
{
    private readonly IOptionsSnapshot<ProductKnowledgeSettings> _options;
    private readonly IOptionsSnapshot<ProductSourceSwitches>? _switches;

    public SourceLimitsProvider(IOptionsSnapshot<ProductKnowledgeSettings> options, IOptionsSnapshot<ProductSourceSwitches>? switches = null)
    {
        _options = options;
        _switches = switches;
    }

    public SourceLimits Get()
    {
        var o = _options.Value;
        return new SourceLimits(
            Math.Clamp(o.MaxFileMb, 1, 200),
            Math.Clamp(o.MaxSourcesPerTenant, 1, 1000),
            Math.Clamp(o.MaxScannedPages, 0, 200),
            Math.Clamp(o.CrawlMaxPages, 1, 500),
            Math.Clamp(o.CrawlMaxDepth, 0, 5),
            Math.Clamp(o.CrawlPageTimeoutSeconds, 3, 60),
            Math.Clamp(o.CrawlMaxPageKb, 64, 10_240),
            o.CrawlRespectRobots,
            _switches?.Value.DocumentUpload ?? true,
            _switches?.Value.WebsiteScanning ?? true,
            Math.Clamp(o.DraftPassages, 2, 12),
            Math.Clamp(o.MaxCustomQuestions, 0, 100));
    }
}
