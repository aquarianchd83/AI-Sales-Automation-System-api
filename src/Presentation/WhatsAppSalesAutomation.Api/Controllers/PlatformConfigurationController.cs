using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Configuration page - PlatformSuperAdmin-only. The billing rules (refund policy,
/// alerts, trial quota, WhatsApp quota weights) and what each metered thing is charged at.</summary>
[ApiController]
[Route("api/v1/platform/configuration")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformConfigurationController : ControllerBase
{
    private readonly IPlatformConfigurationService _configuration;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformConfigurationController(
        IPlatformConfigurationService configuration,
        IPlatformAuditService auditService,
        ICurrentUserService currentUser)
    {
        _configuration = configuration;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PlatformConfigurationDto>> Get(CancellationToken cancellationToken)
        => Ok(await _configuration.GetAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<PlatformConfigurationDto>> Update([FromBody] PlatformConfigurationDto request, CancellationToken cancellationToken)
    {
        var result = await _configuration.UpdateAsync(request, _currentUser.UserId, cancellationToken);

        await _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, PlatformAuditActions.ConfigurationUpdated,
            details: "Billing rules and charges", cancellationToken: cancellationToken);

        return Ok(result);
    }
}
