using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Leads;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly ICurrentUserService _currentUser;

    public LeadsController(ILeadService leadService, ICurrentUserService currentUser)
    {
        _leadService = leadService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<LeadDto>>> GetPaged(
        [FromQuery] PagedRequest request, [FromQuery] string? stage, [FromQuery] string? score, CancellationToken cancellationToken)
        => Ok(await _leadService.GetPagedAsync(request, stage, score, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LeadDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _leadService.GetByIdAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LeadDto>> Update(Guid id, [FromBody] UpdateLeadRequest request, CancellationToken cancellationToken)
    {
        var updatedByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _leadService.UpdateAsync(id, request, updatedByUserId, cancellationToken));
    }

    [HttpPut("{id:guid}/assign")]
    public async Task<ActionResult<LeadDto>> Assign(Guid id, [FromBody] AssignLeadRequest request, CancellationToken cancellationToken)
        => Ok(await _leadService.AssignAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activities")]
    public async Task<ActionResult<LeadActivityDto>> AddActivity(Guid id, [FromBody] AddLeadActivityRequest request, CancellationToken cancellationToken)
    {
        var createdByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _leadService.AddActivityAsync(id, request, createdByUserId, cancellationToken));
    }
}
