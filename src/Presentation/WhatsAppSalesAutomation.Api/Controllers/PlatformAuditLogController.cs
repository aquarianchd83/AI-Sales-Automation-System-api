using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>Platform Admin Console's Audit Log screen (spec item #10) - the accountability trail for
/// every other Platform* controller's write actions. Read-only; entries are written internally by
/// IPlatformAuditService, never through this API.</summary>
[ApiController]
[Route("api/v1/platform/audit-log")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformAuditLogController : ControllerBase
{
    private readonly IPlatformAuditService _auditService;

    public PlatformAuditLogController(IPlatformAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformAuditLogEntryDto>>> GetPaged([FromQuery] PlatformAuditLogQuery query, CancellationToken cancellationToken)
        => Ok(await _auditService.GetPagedAsync(query, cancellationToken));
}
