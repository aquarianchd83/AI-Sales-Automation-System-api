using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Handoffs;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/handoffs")]
[Authorize]
public class HandoffsController : ControllerBase
{
    private readonly IHandoffService _handoffService;
    private readonly ICurrentUserService _currentUser;

    public HandoffsController(IHandoffService handoffService, ICurrentUserService currentUser)
    {
        _handoffService = handoffService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<HandoffDto>>> GetPaged(
        [FromQuery] PagedRequest request, [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await _handoffService.GetPagedAsync(request, status, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HandoffDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _handoffService.GetByIdAsync(id, cancellationToken));

    [HttpPost("{id:guid}/claim")]
    public async Task<ActionResult<HandoffDto>> Claim(Guid id, CancellationToken cancellationToken)
    {
        var agentId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _handoffService.ClaimAsync(id, agentId, cancellationToken));
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<ActionResult<HandoffDto>> Resolve(Guid id, [FromBody] ResolveHandoffRequest request, CancellationToken cancellationToken)
        => Ok(await _handoffService.ResolveAsync(id, request, cancellationToken));
}
