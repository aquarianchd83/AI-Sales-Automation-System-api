namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>Bound from "MediaStorage:S3". The platform owner provides ONE bucket for every tenant, so a tenant never has to
/// open a cloud account: they upload in the Media Library and the files land here, under their own folder.
/// The objects must be publicly readable (Meta downloads them by link) - a bucket policy allowing s3:GetObject on
/// <see cref="KeyPrefix"/>/* does it once for everyone; no per-file permissions are set.</summary>
public class S3MediaStorageSettings
{
    public string BucketName { get; set; } = string.Empty;

    /// <summary>AWS region code, e.g. "ap-southeast-2".</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Leave both keys empty to use the server's own AWS identity (an IAM role on EC2/ECS, or the default
    /// credential chain); the identity needs s3:PutObject, s3:GetObject and s3:DeleteObject on the prefix.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Folder in the bucket all media goes under; the policy above should cover it.</summary>
    public string KeyPrefix { get; set; } = "media";

    /// <summary>Optional CDN/custom domain in front of the bucket (e.g. https://cdn.example.com). Empty uses the bucket's own
    /// https://{bucket}.s3.{region}.amazonaws.com address.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BucketName) && !string.IsNullOrWhiteSpace(Region);
}
