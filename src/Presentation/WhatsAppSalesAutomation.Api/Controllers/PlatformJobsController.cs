using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Platform Admin Console's Background Jobs screen - PlatformSuperAdmin-only, like every other Platform*
/// controller. Each tenant's recurring jobs are listed, scheduled, paused and run from here.
///
/// Deliberately the only API surface for this: a tenant's own Admin has no endpoint to reschedule or
/// pause their background jobs, the same call already made for WhatsApp/AI credentials. The Hangfire
/// dashboard at /hangfire stays available to a PlatformSuperAdmin for the raw, per-execution view this
/// screen deliberately does not try to reproduce.
/// </summary>
[ApiController]
[Route("api/v1/platform/jobs")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformJobsController : ControllerBase
{
    private readonly IPlatformJobService _jobService;
    private readonly ICurrentUserService _currentUser;

    public PlatformJobsController(IPlatformJobService jobService, ICurrentUserService currentUser)
    {
        _jobService = jobService;
        _currentUser = currentUser;
    }

    /// <summary>Every tenant's jobs, one row per tenant per job type, failing ones first.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<PlatformTenantJobDto>>> GetPaged(
        [FromQuery] PlatformJobQuery query, CancellationToken cancellationToken)
        => Ok(await _jobService.GetPagedAsync(query, cancellationToken));

    /// <summary>What a per-tenant job can be - key, display name, description and default schedule - so
    /// the console can render filters and an "reset to default" affordance without duplicating the
    /// catalog.</summary>
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<TenantJobDefinition>> GetCatalog() => Ok(TenantJobCatalog.All);

    /// <summary>The recurring jobs that are platform-global rather than per-tenant, read-only.</summary>
    [HttpGet("platform")]
    public async Task<ActionResult<IReadOnlyList<PlatformGlobalJobDto>>> GetPlatformJobs(CancellationToken cancellationToken)
        => Ok(await _jobService.GetPlatformJobsAsync(cancellationToken));

    /// <summary>One tenant's jobs - what the Tenant detail screen's Background Jobs section shows.</summary>
    [HttpGet("tenants/{tenantId:guid}")]
    public async Task<ActionResult<PlatformTenantJobsDto>> GetForTenant(Guid tenantId, CancellationToken cancellationToken)
        => Ok(await _jobService.GetForTenantAsync(tenantId, cancellationToken));

    /// <summary>Changes one tenant's schedule for one job, or pauses/resumes it. Takes effect
    /// immediately - the Hangfire registration is rebuilt as part of this request.</summary>
    [HttpPut("tenants/{tenantId:guid}/{jobType}")]
    public async Task<ActionResult<PlatformTenantJobDto>> UpdateSchedule(
        Guid tenantId,
        string jobType,
        [FromBody] UpdateTenantJobScheduleRequest request,
        CancellationToken cancellationToken)
        => Ok(await _jobService.UpdateScheduleAsync(tenantId, jobType, request, ActorUserId, ActorEmail, cancellationToken));

    /// <summary>Runs one tenant's copy of a job once, now, without changing its schedule.</summary>
    [HttpPost("tenants/{tenantId:guid}/{jobType}/trigger")]
    public async Task<ActionResult<PlatformJobTriggerResultDto>> Trigger(
        Guid tenantId, string jobType, CancellationToken cancellationToken)
        => Ok(await _jobService.TriggerAsync(tenantId, jobType, ActorUserId, ActorEmail, cancellationToken));

    /// <summary>Rebuilds every tenant's registrations from the schedule table - the same pass that runs at
    /// startup and hourly, on demand.</summary>
    [HttpPost("reconcile")]
    public async Task<ActionResult<TenantJobReconcileSummary>> Reconcile(CancellationToken cancellationToken)
        => Ok(await _jobService.ReconcileAsync(ActorUserId, ActorEmail, cancellationToken));

    private Guid ActorUserId => _currentUser.UserId ?? Guid.Empty;

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
