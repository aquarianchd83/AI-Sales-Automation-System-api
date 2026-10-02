using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's AWS Settings page - PlatformSuperAdmin-only. The platform's S3 media bucket and the AWS
/// credentials for it. Stored in the database only (the access key and secret encrypted); appsettings.json holds none of it.</summary>
[ApiController]
[Route("api/v1/platform/aws-settings")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformAwsSettingsController : ControllerBase
{
    private readonly IPlatformAwsSettingsService _awsSettings;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformAwsSettingsController(
        IPlatformAwsSettingsService awsSettings,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser)
    {
        _awsSettings = awsSettings;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    /// <summary>Secrets are masked: only whether one is stored and its last four characters come back.</summary>
    [HttpGet]
    public async Task<ActionResult<PlatformAwsSettingsDto>> Get(CancellationToken cancellationToken)
        => Ok(await _awsSettings.GetAsync(cancellationToken));

    /// <summary>Tries the settings in the request against AWS (write, read back, delete a small test file) WITHOUT saving them, so an admin
    /// can check before committing. A null access key / secret tests with the stored ones. Always 200: the result says whether it worked.</summary>
    [HttpPost("test-connection")]
    public async Task<ActionResult<AwsConnectionTestResultDto>> TestConnection([FromBody] UpdatePlatformAwsSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await _awsSettings.TestConnectionAsync(request, cancellationToken));

    /// <summary>Takes effect on the next request, no restart. A null access key / secret keeps the stored one; an empty string clears it.</summary>
    [HttpPut]
    public async Task<ActionResult<PlatformAwsSettingsDto>> Update([FromBody] UpdatePlatformAwsSettingsRequest request, CancellationToken cancellationToken)
    {
        var result = await _awsSettings.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        // Names what changed, never the secret values themselves.
        var credentials = request.AccessKeyId is null && request.SecretAccessKey is null ? "credentials unchanged" : "credentials updated";
        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, PlatformAuditActions.AwsSettingsUpdated,
            details: $"Storage provider {result.StorageProvider}, bucket '{result.BucketName}' ({result.Region}); {credentials}", cancellationToken: cancellationToken);

        return Ok(result);
    }
}
