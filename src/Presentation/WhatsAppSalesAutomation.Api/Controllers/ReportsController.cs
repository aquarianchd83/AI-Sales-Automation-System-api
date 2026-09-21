using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Reports;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's reports. Admin and SalesManager: managers reading how campaigns and agents are doing
/// is the point of a report. (The AI agent-performance screen stays Admin-only, because acting on it
/// means changing the qualification schema and scoring rules, which only Admin can do.)
///
/// Every endpoint takes <c>days</c>, the window ending now, clamped to 1-365 and defaulting to 30.
/// There is no tenant parameter: a report is always the caller's own tenant, through the same query
/// filter as everything else.
/// </summary>
[ApiController]
[Route("api/v1/reports")]
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.SalesManager)]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reports;

    public ReportsController(IReportService reports)
    {
        _reports = reports;
    }

    /// <summary>Per campaign: delivery, reads, responses and opt-outs. Optionally one campaign.</summary>
    [HttpGet("campaign-performance")]
    public async Task<ActionResult<CampaignPerformanceReportDto>> CampaignPerformance(
        [FromQuery] int days = ReportService.DefaultDays, [FromQuery] Guid? campaignId = null, CancellationToken cancellationToken = default)
        => Ok(await _reports.GetCampaignPerformanceAsync(days, campaignId, cancellationToken));

    /// <summary>Leads created in the window, by where they are now.</summary>
    [HttpGet("lead-funnel")]
    public async Task<ActionResult<LeadFunnelReportDto>> LeadFunnel(
        [FromQuery] int days = ReportService.DefaultDays, CancellationToken cancellationToken = default)
        => Ok(await _reports.GetLeadFunnelAsync(days, cancellationToken));

    /// <summary>Your human sales agents: leads won, handoffs resolved, time to resolve.</summary>
    [HttpGet("agent-performance")]
    public async Task<ActionResult<HumanAgentPerformanceReportDto>> AgentPerformance(
        [FromQuery] int days = ReportService.DefaultDays, CancellationToken cancellationToken = default)
        => Ok(await _reports.GetAgentPerformanceAsync(days, cancellationToken));

    /// <summary>What the AI agent handled: volume, outcomes, confidence, latency and token cost.</summary>
    [HttpGet("ai-performance")]
    public async Task<ActionResult<AiPerformanceReportDto>> AiPerformance(
        [FromQuery] int days = ReportService.DefaultDays, CancellationToken cancellationToken = default)
        => Ok(await _reports.GetAiPerformanceAsync(days, cancellationToken));
}
