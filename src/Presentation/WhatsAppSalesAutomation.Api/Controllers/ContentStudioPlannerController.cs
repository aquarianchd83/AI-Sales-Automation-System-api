using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Planner;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The AI Content Studio's planner: projects, topic ideas and scripts written from the tenant's verified facts. Admins and sales managers
/// plan content; every call works on the calling tenant only (another tenant's id is "not found"), and is refused unless the platform has
/// the studio switched on for the tenant. Writing topics or a script is charged to the tenant's studio quota and returned on failure.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioPlannerController : ControllerBase
{
    private readonly IContentPlannerService _planner;
    private readonly ITenantContext _tenantContext;

    public ContentStudioPlannerController(IContentPlannerService planner, ITenantContext tenantContext)
    {
        _planner = planner;
        _tenantContext = tenantContext;
    }

    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<ProjectSummaryDto>>> List(CancellationToken cancellationToken)
        => Ok(await _planner.ListAsync(RequireTenantId(), cancellationToken));

    [HttpPost("projects")]
    public async Task<ActionResult<ProjectDetailDto>> Create([FromBody] CreateProjectRequest request, CancellationToken cancellationToken)
        => Ok(await _planner.CreateAsync(RequireTenantId(), request, cancellationToken));

    [HttpGet("projects/{id:guid}")]
    public async Task<ActionResult<ProjectDetailDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _planner.GetAsync(RequireTenantId(), id, cancellationToken));

    [HttpPut("projects/{id:guid}")]
    public async Task<ActionResult<ProjectDetailDto>> Update(Guid id, [FromBody] UpdateProjectRequest request, CancellationToken cancellationToken)
        => Ok(await _planner.UpdateAsync(RequireTenantId(), id, request, cancellationToken));

    [HttpDelete("projects/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _planner.DeleteAsync(RequireTenantId(), id, cancellationToken);
        return NoContent();
    }

    [HttpPost("projects/{id:guid}/topics/generate")]
    public async Task<ActionResult<ProjectDetailDto>> GenerateTopics(Guid id, [FromBody] GenerateTopicsRequest request, CancellationToken cancellationToken)
        => Ok(await _planner.GenerateTopicsAsync(RequireTenantId(), id, request ?? new GenerateTopicsRequest(null), cancellationToken));

    [HttpPut("topics/{id:guid}")]
    public async Task<ActionResult<ProjectDetailDto>> UpdateTopic(Guid id, [FromBody] UpdateTopicRequest request, CancellationToken cancellationToken)
        => Ok(await _planner.UpdateTopicAsync(RequireTenantId(), id, request, cancellationToken));

    [HttpPost("projects/{id:guid}/script/generate")]
    public async Task<ActionResult<ProjectDetailDto>> GenerateScript(Guid id, [FromBody] GenerateScriptRequest request, CancellationToken cancellationToken)
        => Ok(await _planner.GenerateScriptAsync(RequireTenantId(), id, request ?? new GenerateScriptRequest(null, null, null), cancellationToken));

    [HttpGet("versions/{id:guid}")]
    public async Task<ActionResult<ScriptVersionDto>> GetVersion(Guid id, CancellationToken cancellationToken)
        => Ok(await _planner.GetVersionAsync(RequireTenantId(), id, cancellationToken));

    [HttpPost("versions/{id:guid}/claim-check")]
    public async Task<ActionResult<ScriptVersionDto>> Recheck(Guid id, CancellationToken cancellationToken)
        => Ok(await _planner.RecheckAsync(RequireTenantId(), id, cancellationToken));

    [HttpPut("scenes/{id:guid}")]
    public async Task<ActionResult<ScriptVersionDto>> UpdateScene(Guid id, [FromBody] UpdateSceneRequest request, CancellationToken cancellationToken)
        => Ok(await _planner.UpdateSceneAsync(RequireTenantId(), id, request, cancellationToken));

    [HttpDelete("scenes/{id:guid}")]
    public async Task<ActionResult<ScriptVersionDto>> DeleteScene(Guid id, CancellationToken cancellationToken)
        => Ok(await _planner.DeleteSceneAsync(RequireTenantId(), id, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content-studio request has no tenant in scope.");
}
