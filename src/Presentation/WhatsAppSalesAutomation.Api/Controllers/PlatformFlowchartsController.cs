using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Content Management System - the Flowcharts screen. <see cref="GetPublished"/> is open to any
/// authenticated user of any tenant; every other action is PlatformSuperAdmin-only.
/// </summary>
[ApiController]
[Route("api/v1/platform/flowcharts")]
[Authorize]
public class PlatformFlowchartsController : ControllerBase
{
    private readonly IFlowchartService _flowchartService;
    private readonly ICurrentUserService _currentUser;

    public PlatformFlowchartsController(IFlowchartService flowchartService, ICurrentUserService currentUser)
    {
        _flowchartService = flowchartService;
        _currentUser = currentUser;
    }

    [HttpGet("published")]
    public async Task<ActionResult<IReadOnlyList<FlowchartDto>>> GetPublished(CancellationToken cancellationToken)
        => Ok(await _flowchartService.GetPublishedAsync(cancellationToken));

    [HttpGet]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<IReadOnlyList<FlowchartDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _flowchartService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<FlowchartDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _flowchartService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<FlowchartDto>> Create([FromBody] CreateFlowchartRequest request, CancellationToken cancellationToken)
    {
        var created = await _flowchartService.CreateAsync(request, ActorUserId, ActorEmail, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<FlowchartDto>> Update(Guid id, [FromBody] UpdateFlowchartRequest request, CancellationToken cancellationToken)
        => Ok(await _flowchartService.UpdateAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _flowchartService.DeleteAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
