using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The Platform Admin Console's Service margins screen - PlatformSuperAdmin-only. For AI conversations, discovered customers and WhatsApp messages:
/// what each costs the platform (worked out), what the tenant is charged (set here) and the margin between them.</summary>
[ApiController]
[Route("api/v1/platform/service-margins")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformServiceMarginsController : ControllerBase
{
    private readonly IServiceMarginService _margins;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformServiceMarginsController(IServiceMarginService margins, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _margins = margins;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<ServiceMarginDto>> Get(CancellationToken cancellationToken)
        => Ok(await _margins.GetAsync(cancellationToken));

    /// <summary>Sets the credits each service draws. Applies to what happens next; past charges are not changed.</summary>
    [HttpPut]
    public async Task<ActionResult<ServiceMarginDto>> Update([FromBody] UpdateServiceMarginRequest request, CancellationToken cancellationToken)
    {
        var actor = _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");
        var before = await _margins.GetAsync(cancellationToken);
        var updated = await _margins.UpdateAsync(request, actor, cancellationToken);

        string Describe(ServiceMarginDto d) =>
            string.Join(", ", d.Rows.Select(r => $"{r.Title} {r.CreditsPerUnit:0.####} credit(s) per {r.UnitLabel}"));

        await _auditService.LogAsync(
            actor, _currentUser.Email ?? string.Empty, PlatformAuditActions.ServiceChargesChanged, null,
            details: $"{Describe(before)} -> {Describe(updated)}", cancellationToken: cancellationToken);

        return Ok(updated);
    }
}
