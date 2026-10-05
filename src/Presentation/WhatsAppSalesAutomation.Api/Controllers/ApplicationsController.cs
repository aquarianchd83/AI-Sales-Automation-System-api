using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Setup;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// A tenant's plan-driven applications and their setup. Anyone in the tenant can look; configuring and running one
/// is for Admins and Sales Managers. Running is refused (409, with the outstanding fields) until the setup is complete.
/// </summary>
[ApiController]
[Route("api/v1/applications")]
[Authorize]
public class ApplicationsController : ControllerBase
{
    private const string Operators = AppRoles.Admin + "," + AppRoles.SalesManager;

    private readonly IApplicationSetupService _service;

    public ApplicationsController(IApplicationSetupService service)
    {
        _service = service;
    }

    /// <summary>Plans an application can be started on, each with its current setup version.</summary>
    [HttpGet("plans")]
    public async Task<ActionResult<IReadOnlyList<AvailablePlanDto>>> GetPlans(CancellationToken cancellationToken)
        => Ok(await _service.GetAvailablePlansAsync(cancellationToken));

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _service.GetApplicationsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApplicationDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _service.GetApplicationAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Roles = Operators)]
    public async Task<ActionResult<ApplicationDto>> Create([FromBody] CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>The setup wizard: this application's questions (from its plan version), answers and progress.</summary>
    [HttpGet("{id:guid}/setup")]
    public async Task<ActionResult<ApplicationSetupDto>> GetSetup(Guid id, CancellationToken cancellationToken)
        => Ok(await _service.GetSetupAsync(id, cancellationToken));

    /// <summary>Save &amp; Continue / Save &amp; Exit (<c>complete: false</c>) or confirm the setup (<c>complete: true</c>).</summary>
    [HttpPut("{id:guid}/setup")]
    [Authorize(Roles = Operators)]
    public async Task<ActionResult<SaveSetupResultDto>> SaveSetup(Guid id, [FromBody] SaveSetupRequest request, CancellationToken cancellationToken)
        => Ok(await _service.SaveSetupAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/change-plan")]
    [Authorize(Roles = Operators)]
    public async Task<ActionResult<ApplicationSetupDto>> ChangePlan(Guid id, [FromBody] ChangePlanRequest request, CancellationToken cancellationToken)
        => Ok(await _service.ChangePlanAsync(id, request, cancellationToken));

    /// <summary>Moves the application to the newest published version of its plan - never done implicitly.</summary>
    [HttpPost("{id:guid}/migrate")]
    [Authorize(Roles = Operators)]
    public async Task<ActionResult<ApplicationSetupDto>> Migrate(Guid id, CancellationToken cancellationToken)
        => Ok(await _service.MigrateToLatestVersionAsync(id, cancellationToken));

    /// <summary>Runs the application. 409 with the outstanding fields when its setup is not complete.</summary>
    [HttpPost("{id:guid}/execute")]
    [Authorize(Roles = Operators)]
    public async Task<ActionResult<ApplicationExecutionDto>> Execute(Guid id, CancellationToken cancellationToken)
    {
        var result = await _service.ExecuteAsync(id, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpGet("{id:guid}/executions")]
    public async Task<ActionResult<IReadOnlyList<ApplicationExecutionDto>>> GetExecutions(Guid id, CancellationToken cancellationToken)
        => Ok(await _service.GetExecutionsAsync(id, cancellationToken));

    [HttpGet("{id:guid}/audit")]
    public async Task<ActionResult<PagedResult<SetupAuditEntryDto>>> GetAudit(Guid id, [FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _service.GetAuditAsync(id, request, cancellationToken));
}
