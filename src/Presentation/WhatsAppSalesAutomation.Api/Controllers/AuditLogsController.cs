using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's audit trail: who changed a lead's stage, started or stopped a campaign, switched a
/// conversation between AI and human, reassigned a handoff, or recorded an opt-out.
///
/// Read-only, and Admin-only. Read-only because there is nothing here to write - entries are produced
/// by the data layer in the same transaction as the change they describe. Admin-only because the trail
/// names individual staff and their IP addresses.
/// </summary>
[ApiController]
[Route("api/v1/audit-logs")]
[Authorize(Roles = AppRoles.Admin)]
public class AuditLogsController : ControllerBase
{
    private readonly IAuditLogService _auditLogs;

    public AuditLogsController(IAuditLogService auditLogs)
    {
        _auditLogs = auditLogs;
    }

    /// <summary>Newest first. Combine <c>entityName</c> and <c>entityId</c> for the history of one record.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditLogEntryDto>>> Get(
        [FromQuery] AuditLogQuery query, CancellationToken cancellationToken)
        => Ok(await _auditLogs.GetPagedAsync(query, cancellationToken));
}
