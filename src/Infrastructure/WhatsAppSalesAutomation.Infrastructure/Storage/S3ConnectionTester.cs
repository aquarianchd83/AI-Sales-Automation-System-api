using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>Proves a bucket and credentials work the way media uploads use them: writes a tiny object under the folder, reads it
/// back, then deletes it. Each step reports on its own, so an admin sees "can write but not delete" rather than a bare failure.
/// The test object is always cleaned up when it was written.</summary>
public class S3ConnectionTester : IAwsConnectionTester
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<AwsConnectionTestResultDto> TestAsync(AwsConnectionTestSettings settings, CancellationToken cancellationToken = default)
    {
        var steps = new List<AwsConnectionStepDto>();

        RegionEndpoint region;
        try
        {
            region = RegionEndpoint.GetBySystemName(settings.Region);
        }
        catch (Exception ex)
        {
            return Failed(steps, "Connect", $"'{settings.Region}' is not a region AWS recognises: {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;

        using var client = settings.AccessKeyId.Length > 0 && settings.SecretAccessKey.Length > 0
            ? new AmazonS3Client(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey), region)
            : new AmazonS3Client(region);

        var key = $"{settings.KeyPrefix.Trim('/')}/_connection-test/{Guid.NewGuid():N}.txt";
        const string body = "connection test";
        var written = false;

        try
        {
            try
            {
                await client.PutObjectAsync(new PutObjectRequest { BucketName = settings.BucketName, Key = key, ContentBody = body, ContentType = "text/plain" }, token);
                written = true;
                steps.Add(new AwsConnectionStepDto("Write", true, $"Wrote {key}"));
            }
            catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or OperationCanceledException)
            {
                return Failed(steps, "Write", Explain(ex, settings, settings.AccessKeyId.Length > 0, "s3:PutObject"));
            }

            try
            {
                using var response = await client.GetObjectAsync(settings.BucketName, key, token);
                using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
                var content = await reader.ReadToEndAsync(token);
                steps.Add(content == body
                    ? new AwsConnectionStepDto("Read", true, "Read the test file back")
                    : new AwsConnectionStepDto("Read", false, "The file read back was different from what was written"));
            }
            catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or OperationCanceledException)
            {
                steps.Add(new AwsConnectionStepDto("Read", false, Explain(ex, settings, settings.AccessKeyId.Length > 0, "s3:GetObject")));
            }
        }
        finally
        {
            if (written)
            {
                try
                {
                    // Cleanup must still run when the caller gave up, so it uses its own short budget.
                    using var cleanup = new CancellationTokenSource(Timeout);
                    await client.DeleteObjectAsync(settings.BucketName, key, cleanup.Token);
                    steps.Add(new AwsConnectionStepDto("Delete", true, "Removed the test file"));
                }
                catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or OperationCanceledException)
                {
                    steps.Add(new AwsConnectionStepDto("Delete", false, Explain(ex, settings, settings.AccessKeyId.Length > 0, "s3:DeleteObject") + $" The test file {key} may need removing by hand."));
                }
            }
        }

        var ok = steps.All(s => s.Passed);
        return new AwsConnectionTestResultDto(
            ok,
            ok ? $"Connected to {settings.BucketName} ({settings.Region}): media can be written, read and deleted." : "The connection works only in part - see the steps below.",
            steps);
    }

    private static AwsConnectionTestResultDto Failed(List<AwsConnectionStepDto> steps, string name, string detail)
    {
        steps.Add(new AwsConnectionStepDto(name, false, detail));
        return new AwsConnectionTestResultDto(false, detail, steps);
    }

    /// <summary>Turns AWS's low-level failures into a sentence the platform admin can act on.</summary>
    private static string Explain(Exception ex, AwsConnectionTestSettings settings, bool hasKeys, string permission) => ex switch
    {
        OperationCanceledException => "AWS did not answer in time - check the region and the server's network access to AWS.",
        AmazonS3Exception s3 => s3.ErrorCode switch
        {
            "NoSuchBucket" => $"Bucket '{settings.BucketName}' does not exist in {settings.Region}.",
            "InvalidAccessKeyId" or "SignatureDoesNotMatch" => "The access key ID or secret access key is wrong.",
            "AccessDenied" or "AllAccessDisabled" => $"The AWS identity is not allowed to do this - it needs {permission} on '{settings.KeyPrefix}/*'.",
            "PermanentRedirect" or "AuthorizationHeaderMalformed" => $"The bucket is not in region {settings.Region}.",
            _ => s3.Message,
        },
        AmazonClientException when !hasKeys => "No AWS credentials were found: enter an access key ID and secret, or run the server with an AWS role.",
        _ => ex.Message,
    };
}
