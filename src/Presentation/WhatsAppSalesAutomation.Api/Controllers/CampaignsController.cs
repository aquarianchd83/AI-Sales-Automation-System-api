using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Messaging;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/campaigns")]
[Authorize]
public class CampaignsController : ControllerBase
{
    private readonly ICampaignService _campaignService;
    private readonly ICurrentUserService _currentUser;

    public CampaignsController(ICampaignService campaignService, ICurrentUserService currentUser)
    {
        _campaignService = campaignService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<CampaignDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _campaignService.GetPagedAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CampaignDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _campaignService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CampaignDto>> Create([FromBody] CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        var createdBy = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        var result = await _campaignService.CreateAsync(request, createdBy, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CampaignDto>> Update(Guid id, [FromBody] UpdateCampaignRequest request, CancellationToken cancellationToken)
        => Ok(await _campaignService.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _campaignService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Adds the step at this StepType's position, or replaces it if one already exists there.</summary>
    [HttpPost("{id:guid}/steps")]
    public async Task<ActionResult<CampaignDto>> UpsertStep(Guid id, [FromBody] UpsertCampaignStepRequest request, CancellationToken cancellationToken)
        => Ok(await _campaignService.UpsertStepAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}/steps/{stepType}")]
    public async Task<ActionResult<CampaignDto>> RemoveStep(Guid id, string stepType, CancellationToken cancellationToken)
        => Ok(await _campaignService.RemoveStepAsync(id, stepType, cancellationToken));

    [HttpPost("{id:guid}/audience")]
    public async Task<ActionResult<SetCampaignAudienceResultDto>> SetAudience(Guid id, [FromBody] SetCampaignAudienceRequest request, CancellationToken cancellationToken)
        => Ok(await _campaignService.SetAudienceAsync(id, request, cancellationToken));

    /// <summary>The roster behind SetAudience's counts - who is attached, and their status/step.</summary>
    [HttpGet("{id:guid}/audience")]
    public async Task<ActionResult<PagedResult<CampaignAudienceMemberDto>>> GetAudience(Guid id, [FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _campaignService.GetAudienceAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/start")]
    public async Task<ActionResult<CampaignDto>> Start(Guid id, CancellationToken cancellationToken)
        => Ok(await _campaignService.StartAsync(id, cancellationToken));

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<CampaignDto>> Pause(Guid id, CancellationToken cancellationToken)
        => Ok(await _campaignService.PauseAsync(id, cancellationToken));

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<CampaignDto>> Resume(Guid id, CancellationToken cancellationToken)
        => Ok(await _campaignService.ResumeAsync(id, cancellationToken));

    [HttpPost("{id:guid}/stop")]
    public async Task<ActionResult<CampaignDto>> Stop(Guid id, CancellationToken cancellationToken)
        => Ok(await _campaignService.StopAsync(id, cancellationToken));

    [HttpGet("{id:guid}/progress")]
    public async Task<ActionResult<CampaignProgressDto>> GetProgress(Guid id, CancellationToken cancellationToken)
        => Ok(await _campaignService.GetProgressAsync(id, cancellationToken));
}

/// <summary>
/// Runs the send pipeline immediately instead of waiting for Hangfire's next tick - useful in dev/
/// test where waiting a real minute per step is friction, and occasionally in production to nudge a
/// backlog. Kept as its own controller/route group (rather than nested under CampaignsController) so
/// its narrower SuperAdmin-only authorization is visible at a glance rather than mixed in with the
/// broader campaign CRUD policy.
/// </summary>
[ApiController]
[Route("api/v1/campaigns/ops")]
[Authorize(Roles = "SuperAdmin")]
public class CampaignOpsController : ControllerBase
{
    private readonly ICampaignSendService _sendService;

    public CampaignOpsController(ICampaignSendService sendService)
    {
        _sendService = sendService;
    }

    [HttpPost("run-jobs")]
    public async Task<ActionResult<RunJobsResultDto>> RunJobs(CancellationToken cancellationToken)
    {
        var initial = await _sendService.ProcessInitialSendsAsync(cancellationToken);
        var followUps = await _sendService.ProcessFollowUpsAsync(cancellationToken);
        var retries = await _sendService.RetryFailedSendsAsync(cancellationToken);

        return Ok(new RunJobsResultDto(initial, followUps, retries));
    }
}

public record RunJobsResultDto(SendRunResult InitialSends, SendRunResult FollowUps, SendRunResult Retries);
