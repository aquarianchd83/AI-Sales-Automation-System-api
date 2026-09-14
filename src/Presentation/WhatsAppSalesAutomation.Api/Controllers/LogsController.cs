using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.LogViewer;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A queryable view over the API's own Serilog file output (logs/log-*.txt) - so a _logger.LogWarning
/// / LogError / etc. call is readable without opening a multi-megabyte text file by hand.
///
/// PlatformSuperAdmin-only: the log file is global and unscoped by tenant - a single day's file can
/// carry any tenant's raw webhook payloads, errors, etc. Tenant-scoped SuperAdmin/Admin used to be
/// allowed here, which was a cross-tenant data leak (any tenant admin could read every other tenant's
/// log lines); that access will come back as its own tenant-scoped log view later, not by reopening
/// this endpoint.
/// </summary>
[ApiController]
[Route("api/v1/logs")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class LogsController : ControllerBase
{
    private readonly ILogService _logService;

    public LogsController(ILogService logService)
    {
        _logService = logService;
    }

    /// <summary>Defaults to today (IST) when no date is given. See LogQueryRequest for the
    /// level/module/method/search filters - module and method identify which class and method actually
    /// logged the line (e.g. "ConversationService" / "SendMessageAsync"), not just its text.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<LogEntryDto>>> GetPaged([FromQuery] LogQueryRequest request, CancellationToken cancellationToken)
        => Ok(await _logService.GetPagedAsync(request, cancellationToken));

    /// <summary>Calendar dates that have at least one log file, newest first - drives a date picker.</summary>
    [HttpGet("dates")]
    public ActionResult<IReadOnlyList<DateOnly>> GetAvailableDates()
        => Ok(_logService.GetAvailableDates());

    /// <summary>Distinct Module values present in the given date's log file (defaults to today IST) -
    /// drives a module picker.</summary>
    [HttpGet("modules")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetAvailableModules([FromQuery] DateOnly? date, CancellationToken cancellationToken)
        => Ok(await _logService.GetAvailableModulesAsync(date, cancellationToken));
}
