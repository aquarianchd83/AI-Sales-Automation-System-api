using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Planner;
using WhatsAppSalesAutomation.Application.ContentStudio.Review;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Review and approval of scripts in the AI Content Studio. Admins and sales managers may send a script for approval, take it back, and start
/// a revision; approving or rejecting is checked again in the service against the tenant's approval policy (an Admin always; a Sales Manager only
/// if the policy allows). Every call works on the calling tenant only (another tenant's id is "not found").
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio/versions")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioReviewController : ControllerBase
{
    private readonly IContentReviewService _review;
    private readonly IContentPlannerService _planner;
    private readonly ITenantContext _tenantContext;

    public ContentStudioReviewController(IContentReviewService review, IContentPlannerService planner, ITenantContext tenantContext)
    {
        _review = review;
        _planner = planner;
        _tenantContext = tenantContext;
    }

    [HttpGet("{id:guid}/review")]
    public async Task<ActionResult<ReviewStateDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _review.GetAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<ReviewStateDto>> Submit(Guid id, [FromBody] ReviewActionRequest request, CancellationToken cancellationToken)
        => Ok(await _review.SubmitAsync(RequireTenantId(), id, request ?? new ReviewActionRequest(null), cancellationToken));

    [HttpPost("{id:guid}/withdraw")]
    public async Task<ActionResult<ReviewStateDto>> Withdraw(Guid id, [FromBody] ReviewActionRequest request, CancellationToken cancellationToken)
        => Ok(await _review.WithdrawAsync(RequireTenantId(), id, request ?? new ReviewActionRequest(null), cancellationToken));

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<ReviewStateDto>> Approve(Guid id, [FromBody] ReviewActionRequest request, CancellationToken cancellationToken)
        => Ok(await _review.ApproveAsync(RequireTenantId(), id, request ?? new ReviewActionRequest(null), cancellationToken));

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<ReviewStateDto>> Reject(Guid id, [FromBody] RejectRequest request, CancellationToken cancellationToken)
        => Ok(await _review.RejectAsync(RequireTenantId(), id, request ?? new RejectRequest(null), cancellationToken));

    /// <summary>Starts a new draft from an approved or rejected script and returns the project, with the new draft as its current script.</summary>
    [HttpPost("{id:guid}/revise")]
    public async Task<ActionResult<ProjectDetailDto>> Revise(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var newVersionId = await _review.ReviseAsync(tenantId, id, cancellationToken);
        var version = await _planner.GetVersionAsync(tenantId, newVersionId, cancellationToken);
        return Ok(await _planner.GetAsync(tenantId, version.ProjectId, cancellationToken));
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content-studio request has no tenant in scope.");
}
