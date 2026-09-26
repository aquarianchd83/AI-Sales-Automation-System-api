using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant's own view onto its background jobs - campaign sending and lead discovery only (see
/// <see cref="TenantJobCatalog.SelfServiceKeys"/>); WhatsApp integration plumbing stays
/// PlatformSuperAdmin-only via <see cref="PlatformJobsController"/>.
///
/// Reuses <see cref="IPlatformJobService"/> as-is rather than a parallel implementation: every method
/// this needs is already scoped to one tenantId, validates the cron, re-syncs Hangfire and audits the
/// change as one operation - this controller only narrows which job types a tenant Admin is allowed to
/// name and trims the platform-identity fields (TenantId/TenantName/TenantSlug/TenantStatus) off the
/// response, since a tenant already knows who it is.
/// </summary>
[ApiController]
[Route("api/v1/jobs")]
[Authorize(Roles = AppRoles.Admin)]
public class TenantJobsController : ControllerBase
{
    private readonly IPlatformJobService _jobService;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public TenantJobsController(IPlatformJobService jobService, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _jobService = jobService;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    /// <summary>This tenant's campaign-sending and lead-discovery jobs - schedule, enabled state and
    /// last-run outcome for each.</summary>
    [HttpGet]
    public async Task<ActionResult<TenantJobsDto>> Get(CancellationToken cancellationToken)
    {
        var jobs = await _jobService.GetForTenantAsync(TenantId, cancellationToken);
        return Ok(jobs.ToTenantDto(TenantJobCatalog.SelfServiceKeys));
    }

    /// <summary>Changes this tenant's schedule for one job, or pauses/resumes it. Takes effect
    /// immediately - the Hangfire registration is rebuilt as part of this request.</summary>
    [HttpPut("{jobType}")]
    public async Task<ActionResult<TenantJobDto>> UpdateSchedule(
        string jobType, [FromBody] UpdateTenantJobScheduleRequest request, CancellationToken cancellationToken)
    {
        RequireSelfServiceJobType(jobType);
        var job = await _jobService.UpdateScheduleAsync(TenantId, jobType, request, ActorUserId, ActorEmail, cancellationToken);
        return Ok(job.ToTenantDto());
    }

    /// <summary>Runs this tenant's copy of a job once, now, without changing its schedule. Refused for a
    /// disabled job (enable it first) - see <see cref="IPlatformJobService.TriggerAsync"/>.</summary>
    [HttpPost("{jobType}/trigger")]
    public async Task<ActionResult<PlatformJobTriggerResultDto>> Trigger(string jobType, CancellationToken cancellationToken)
    {
        RequireSelfServiceJobType(jobType);
        return Ok(await _jobService.TriggerAsync(TenantId, jobType, ActorUserId, ActorEmail, cancellationToken));
    }

    private static void RequireSelfServiceJobType(string jobType)
    {
        if (!TenantJobCatalog.SelfServiceKeys.Contains(jobType))
            throw new NotFoundException($"'{jobType}' is not a job tenants can manage.");
    }

    private Guid TenantId =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Job management requires a tenant in scope.");

    private Guid ActorUserId => _currentUser.UserId ?? Guid.Empty;

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
