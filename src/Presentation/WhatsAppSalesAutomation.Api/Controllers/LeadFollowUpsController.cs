using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Leads.FollowUps;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The "follow up later" list - leads who were interested but could not go ahead yet, each with a date
/// to be contacted again. The reminders themselves go out from the lead-follow-ups job.</summary>
[ApiController]
[Authorize]
public class LeadFollowUpsController : ControllerBase
{
    private readonly ILeadFollowUpService _followUps;
    private readonly ICurrentUserService _currentUser;

    public LeadFollowUpsController(ILeadFollowUpService followUps, ICurrentUserService currentUser)
    {
        _followUps = followUps;
        _currentUser = currentUser;
    }

    /// <summary>Scheduled follow-ups, soonest due first. <paramref name="status"/> switches to a history view
    /// (Sent, Cancelled, Skipped, Failed); <paramref name="dueWithinDays"/> narrows Scheduled to what is due
    /// within that many days, overdue included.</summary>
    [HttpGet("api/v1/lead-follow-ups")]
    public async Task<ActionResult<PagedResult<LeadFollowUpDto>>> GetPaged(
        [FromQuery] PagedRequest request, [FromQuery] string? status, [FromQuery] int? dueWithinDays, CancellationToken cancellationToken)
        => Ok(await _followUps.GetPagedAsync(request, status, dueWithinDays, cancellationToken));

    [HttpGet("api/v1/lead-follow-ups/summary")]
    public async Task<ActionResult<LeadFollowUpSummaryDto>> GetSummary(CancellationToken cancellationToken)
        => Ok(await _followUps.GetSummaryAsync(cancellationToken));

    [HttpGet("api/v1/leads/{leadId:guid}/follow-ups")]
    public async Task<ActionResult<IReadOnlyList<LeadFollowUpDto>>> GetForLead(Guid leadId, CancellationToken cancellationToken)
        => Ok(await _followUps.GetForLeadAsync(leadId, cancellationToken));

    [HttpPost("api/v1/leads/{leadId:guid}/follow-ups")]
    public async Task<ActionResult<LeadFollowUpDto>> Schedule(Guid leadId, [FromBody] ScheduleLeadFollowUpRequest request, CancellationToken cancellationToken)
        => Ok(await _followUps.ScheduleAsync(leadId, request, CurrentUserId(), cancellationToken));

    [HttpPost("api/v1/lead-follow-ups/{id:guid}/cancel")]
    public async Task<ActionResult<LeadFollowUpDto>> Cancel(Guid id, CancellationToken cancellationToken)
        => Ok(await _followUps.CancelAsync(id, CurrentUserId(), cancellationToken));

    [HttpPost("api/v1/lead-follow-ups/{id:guid}/send-now")]
    public async Task<ActionResult<LeadFollowUpDto>> SendNow(Guid id, CancellationToken cancellationToken)
        => Ok(await _followUps.SendNowAsync(id, cancellationToken));

    private Guid CurrentUserId() =>
        _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
}
