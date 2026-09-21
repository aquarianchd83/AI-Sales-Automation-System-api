using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// How the AI sales agent is performing: what it handled, which qualification questions are working,
/// which output checks are firing, and whether a hot lead actually closes better than the rest.
///
/// Admin-only, matching every other role-gated endpoint here. Not because the numbers are sensitive,
/// but because acting on them means changing the qualification schema or the scoring rules, and Admin
/// is the role that can do either. Opening it to SalesManager is a one-word change if the team wants
/// managers reading it - this repo just has no SalesManager-gated endpoint to follow.
/// </summary>
[ApiController]
[Route("api/v1/agent-performance")]
[Authorize(Roles = AppRoles.Admin)]
public class AgentPerformanceController : ControllerBase
{
    private readonly IAgentPerformanceService _performance;

    public AgentPerformanceController(IAgentPerformanceService performance)
    {
        _performance = performance;
    }

    /// <summary><paramref name="days"/> is the window ending now; the service clamps it.</summary>
    [HttpGet]
    public async Task<ActionResult<AgentPerformanceReportDto>> Get(
        [FromQuery] int days = 30, CancellationToken cancellationToken = default)
        => Ok(await _performance.GetReportAsync(days, cancellationToken));
}
