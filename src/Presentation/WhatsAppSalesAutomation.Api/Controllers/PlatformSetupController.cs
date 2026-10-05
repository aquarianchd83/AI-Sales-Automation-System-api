using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Setup;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Setup Plans screen: what each plan asks a tenant to set up, versioned, with no code
/// change needed to add a plan or a question. PlatformSuperAdmin-only; every write lands in the platform audit log.</summary>
[ApiController]
[Route("api/v1/platform/setup")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformSetupController : ControllerBase
{
    private readonly ISetupAdminService _service;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformSetupController(ISetupAdminService service, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _service = service;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet("plans")]
    public async Task<ActionResult<IReadOnlyList<PlanSetupSummaryDto>>> GetPlans(CancellationToken cancellationToken)
        => Ok(await _service.GetPlansAsync(cancellationToken));

    [HttpPost("plans/{planId:guid}/versions")]
    public async Task<ActionResult<SetupVersionDetailDto>> CreateVersion(Guid planId, [FromBody] CreateSetupVersionRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateVersionAsync(planId, request, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupVersionCreated, $"{result.PlanName} v{result.VersionNumber} (draft)", cancellationToken);
        return CreatedAtAction(nameof(GetVersion), new { versionId = result.Id }, result);
    }

    [HttpGet("versions/{versionId:guid}")]
    public async Task<ActionResult<SetupVersionDetailDto>> GetVersion(Guid versionId, CancellationToken cancellationToken)
        => Ok(await _service.GetVersionAsync(versionId, cancellationToken));

    /// <summary>What a Talent would see for this version - active fields only. Nothing is saved.</summary>
    [HttpGet("versions/{versionId:guid}/preview")]
    public async Task<ActionResult<SetupDefinitionDto>> Preview(Guid versionId, CancellationToken cancellationToken)
        => Ok(await _service.GetPreviewAsync(versionId, cancellationToken));

    [HttpPut("versions/{versionId:guid}")]
    public async Task<ActionResult<SetupVersionDetailDto>> UpdateVersion(Guid versionId, [FromBody] SaveSetupVersionRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateVersionAsync(versionId, request, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupVersionUpdated, $"{result.PlanName} v{result.VersionNumber}", cancellationToken);
        return Ok(result);
    }

    [HttpPost("versions/{versionId:guid}/publish")]
    public async Task<ActionResult<SetupVersionDetailDto>> Publish(Guid versionId, CancellationToken cancellationToken)
    {
        var result = await _service.PublishVersionAsync(versionId, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupVersionPublished, $"{result.PlanName} v{result.VersionNumber}", cancellationToken);
        return Ok(result);
    }

    [HttpDelete("versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid versionId, CancellationToken cancellationToken)
    {
        await _service.DeleteVersionAsync(versionId, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupVersionDeleted, versionId.ToString(), cancellationToken);
        return NoContent();
    }

    [HttpPost("versions/{versionId:guid}/requirements")]
    public async Task<ActionResult<SetupFieldDto>> AddRequirement(Guid versionId, [FromBody] SaveRequirementRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.AddRequirementAsync(versionId, request, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupRequirementSaved, $"Added '{result.FieldKey}' to version {versionId}", cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpPut("requirements/{requirementId:guid}")]
    public async Task<ActionResult<SetupFieldDto>> UpdateRequirement(Guid requirementId, [FromBody] SaveRequirementRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateRequirementAsync(requirementId, request, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupRequirementSaved, $"Updated '{result.FieldKey}' ({requirementId})", cancellationToken);
        return Ok(result);
    }

    [HttpDelete("requirements/{requirementId:guid}")]
    public async Task<IActionResult> DeleteRequirement(Guid requirementId, CancellationToken cancellationToken)
    {
        await _service.DeleteRequirementAsync(requirementId, cancellationToken);
        await LogAsync(PlatformAuditActions.SetupRequirementDeleted, requirementId.ToString(), cancellationToken);
        return NoContent();
    }

    private Task LogAsync(string action, string details, CancellationToken cancellationToken) =>
        _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."),
            _currentUser.Email ?? string.Empty, action, details: details, cancellationToken: cancellationToken);
}
