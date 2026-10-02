namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>Bound from "MediaStorage:S3". The platform owner provides ONE bucket for every tenant, so a tenant never has to
/// open a cloud account: they upload in the Media Library and the files land here, under their own folder.
/// The objects must be publicly readable (Meta downloads them by link) - a bucket policy allowing s3:GetObject on
/// <see cref="KeyPrefix"/>/* does it once for everyone; no per-file permissions are set.
/// Every value is trimmed: a stray space pasted into appsettings ("my-bucket ") otherwise makes AWS reject the bucket name.</summary>
public class S3MediaStorageSettings
{
    private string _bucketName = string.Empty;
    private string _region = string.Empty;
    private string _accessKeyId = string.Empty;
    private string _secretAccessKey = string.Empty;
    private string _keyPrefix = "media";
    private string _publicBaseUrl = string.Empty;

    public string BucketName { get => _bucketName; set => _bucketName = value?.Trim() ?? string.Empty; }

    /// <summary>AWS region code, e.g. "ap-southeast-2".</summary>
    public string Region { get => _region; set => _region = value?.Trim() ?? string.Empty; }

    /// <summary>Leave both keys empty to use the server's own AWS identity (an IAM role on EC2/ECS, or the default
    /// credential chain - environment variables or ~/.aws/credentials); the identity needs s3:PutObject, s3:GetObject
    /// and s3:DeleteObject on the prefix. A machine with no AWS identity (a laptop) must have the keys filled in.</summary>
    public string AccessKeyId { get => _accessKeyId; set => _accessKeyId = value?.Trim() ?? string.Empty; }

    public string SecretAccessKey { get => _secretAccessKey; set => _secretAccessKey = value?.Trim() ?? string.Empty; }

    /// <summary>Folder in the bucket all media goes under; the policy above should cover it.</summary>
    public string KeyPrefix { get => _keyPrefix; set => _keyPrefix = value?.Trim() ?? string.Empty; }

    /// <summary>Optional CDN/custom domain in front of the bucket (e.g. https://cdn.example.com). Empty uses the bucket's own
    /// https://{bucket}.s3.{region}.amazonaws.com address.</summary>
    public string PublicBaseUrl { get => _publicBaseUrl; set => _publicBaseUrl = value?.Trim() ?? string.Empty; }

    public bool HasAccessKeys => AccessKeyId.Length > 0 && SecretAccessKey.Length > 0;

    public bool IsConfigured => BucketName.Length > 0 && Region.Length > 0;
}
