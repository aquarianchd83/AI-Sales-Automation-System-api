using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Simulator;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The Simulator dummy data page: the businesses, replies, answers and switches every simulator uses. PlatformSuperAdmin only.</summary>
[ApiController]
[Route("api/v1/platform/simulator-data")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformSimulatorDataController : ControllerBase
{
    private readonly ISimulatorDummyDataService _service;
    private readonly ICurrentUserService _currentUser;

    public PlatformSimulatorDataController(ISimulatorDummyDataService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<SimulatorDummyDataDto>> GetAll(CancellationToken cancellationToken)
        => Ok(await _service.GetAllAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<SimulatorDummyRecordDto>> Create([FromBody] SaveSimulatorDummyRecordRequest request, CancellationToken cancellationToken)
        => Ok(await _service.CreateAsync(request, ActorUserId, ActorEmail, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SimulatorDummyRecordDto>> Update(Guid id, [FromBody] SaveSimulatorDummyRecordRequest request, CancellationToken cancellationToken)
        => Ok(await _service.UpdateAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    [HttpPost("restore-defaults")]
    public async Task<ActionResult<RestoreSimulatorDefaultsResult>> RestoreDefaults(CancellationToken cancellationToken)
        => Ok(await _service.RestoreDefaultsAsync(ActorUserId, ActorEmail, cancellationToken));

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
