using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The tenant's lead discovery profile (Admin only - it spends the tenant's AI credit) and the
/// leads the lead-discovery job has found. Runs happen on the job's schedule; a PlatformSuperAdmin can also
/// start one from the Platform Admin Console's job list.</summary>
[ApiController]
[Route("api/v1/lead-discovery")]
[Authorize]
public class LeadDiscoveryController : ControllerBase
{
    private readonly ILeadDiscoveryService _leadDiscoveryService;
    private readonly ILeadDiscoveryHistoryService _historyService;

    public LeadDiscoveryController(ILeadDiscoveryService leadDiscoveryService, ILeadDiscoveryHistoryService historyService)
    {
        _leadDiscoveryService = leadDiscoveryService;
        _historyService = historyService;
    }

    [HttpGet("profile")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadDiscoveryProfileDto>> GetProfile(CancellationToken cancellationToken)
        => Ok(await _leadDiscoveryService.GetProfileAsync(cancellationToken));

    [HttpPut("profile")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadDiscoveryProfileDto>> SaveProfile([FromBody] SaveLeadDiscoveryProfileRequest request, CancellationToken cancellationToken)
        => Ok(await _leadDiscoveryService.SaveProfileAsync(request, cancellationToken));

    [HttpGet("leads")]
    public async Task<ActionResult<PagedResult<DiscoveredLeadDto>>> GetLeads(
        [FromQuery] PagedRequest request, [FromQuery] int? minScore, CancellationToken cancellationToken)
        => Ok(await _leadDiscoveryService.GetDiscoveredLeadsAsync(request, minScore, cancellationToken));

    /// <summary>What each run cost and produced, newest first. Admin-only, like the profile - it is spend.</summary>
    [HttpGet("runs")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<PagedResult<LeadDiscoveryRunDto>>> GetRuns(
        [FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _leadDiscoveryService.GetRunsAsync(request, cancellationToken));

    /// <summary>This month's and all-time discovery spend, in USD and the tenant's own currency.</summary>
    [HttpGet("spend")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadDiscoverySpendDto>> GetSpend(CancellationToken cancellationToken)
        => Ok(await _leadDiscoveryService.GetSpendAsync(cancellationToken));

    /// <summary>Auto-campaign enrollment outcomes (Started/Skipped/Failed) for discovered customers,
    /// newest first. Admin-only, like the profile it configures.</summary>
    [HttpGet("auto-campaign-history")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<PagedResult<AutoCampaignEnrollmentDto>>> GetAutoCampaignHistory(
        [FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _leadDiscoveryService.GetAutoCampaignEnrollmentsAsync(request, cancellationToken));

    /// <summary>Lead Discovery History: every execution, grouped by processing date (newest first), with its
    /// customer, Auto-Campaign, template, mapping, retry and lock status. Paged by date. Admin-only.</summary>
    [HttpGet("history")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<PagedResult<LeadDiscoveryHistoryDayDto>>> GetHistory(
        [FromQuery] LeadDiscoveryHistoryQuery query, CancellationToken cancellationToken)
        => Ok(await _historyService.GetHistoryAsync(query, cancellationToken));

    /// <summary>One execution in full: per-customer results, template associations and lock transitions.</summary>
    [HttpGet("executions/{id:guid}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadDiscoveryExecutionDetailDto>> GetExecution(Guid id, CancellationToken cancellationToken)
        => Ok(await _historyService.GetExecutionAsync(id, cancellationToken));

    /// <summary>Queues a retry of a RetryPending/PartiallyCompleted/Failed execution. It runs as a new execution
    /// (new Execution ID and lock token) that resumes only the unfinished steps. 409 when not retryable.</summary>
    [HttpPost("executions/{id:guid}/retry")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<LeadDiscoveryRetryQueuedDto>> RetryExecution(Guid id, CancellationToken cancellationToken)
        => Accepted(await _historyService.RequestRetryAsync(id, cancellationToken));
}
