using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Features page - PlatformSuperAdmin-only. Publishes or unpublishes a feature for the whole platform: an
/// unpublished feature is hidden from every tenant and refused by the API, whatever its plan includes.</summary>
[ApiController]
[Route("api/v1/platform/features")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformFeaturesController : ControllerBase
{
    private readonly IPlatformFeatureSwitchService _switches;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformFeaturesController(IPlatformFeatureSwitchService switches, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _switches = switches;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformFeatureSwitchDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _switches.GetAllAsync(cancellationToken));

    /// <summary>Takes effect on the next request, no restart.</summary>
    [HttpPut("{feature}")]
    public async Task<ActionResult<PlatformFeatureSwitchDto>> SetPublished(PlatformFeature feature, [FromBody] SetFeaturePublishedRequest request, CancellationToken cancellationToken)
    {
        var result = await _switches.SetPublishedAsync(feature, request.Published, _currentUser.UserId, cancellationToken);

        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty,
            request.Published ? PlatformAuditActions.FeaturePublished : PlatformAuditActions.FeatureUnpublished,
            details: $"{result.Label} {(request.Published ? "published" : "unpublished")} for all tenants",
            cancellationToken: cancellationToken);

        return Ok(result);
    }
}
