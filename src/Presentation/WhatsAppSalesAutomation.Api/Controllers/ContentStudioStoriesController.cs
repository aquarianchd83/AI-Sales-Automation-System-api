using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The cinematic story studio: reusable characters, story projects, and the script that must be approved before any paid video work.
/// Works on the calling tenant only. Characters of other tenants are visible only when explicitly shared.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio/stories")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioStoriesController : ControllerBase
{
    private readonly IStoryStudioService _stories;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public ContentStudioStoriesController(IStoryStudioService stories, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _stories = stories;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    [HttpGet("characters")]
    public async Task<ActionResult<IReadOnlyList<StoryCharacterDto>>> ListCharacters([FromQuery] string? search, CancellationToken ct)
        => Ok(await _stories.ListCharactersAsync(RequireTenantId(), search, ct));

    [HttpPost("characters")]
    public async Task<ActionResult<StoryCharacterDto>> CreateCharacter([FromBody] CreateStoryCharacterRequest request, CancellationToken ct)
        => Ok(await _stories.CreateCharacterAsync(RequireTenantId(), _currentUser.UserId, request, ct));

    [HttpDelete("characters/{id:guid}")]
    public async Task<IActionResult> ArchiveCharacter(Guid id, CancellationToken ct)
    {
        await _stories.ArchiveCharacterAsync(RequireTenantId(), id, ct);
        return NoContent();
    }

    [HttpPost("characters/{id:guid}/references")]
    [RequestSizeLimit(StoryStudioService.MaxImageBytes + 1024 * 1024)]
    public async Task<ActionResult<StoryReferenceDto>> UploadReference(Guid id, IFormFile file, [FromForm] bool rightsConfirmed, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose an image to upload." });
        await using var stream = file.OpenReadStream();
        return Ok(await _stories.AddReferenceAsync(RequireTenantId(), _currentUser.UserId, id, stream, file.FileName, rightsConfirmed, ct));
    }

    [HttpPost("characters/{id:guid}/references/{referenceId:guid}/approve")]
    public async Task<ActionResult<StoryReferenceDto>> ApproveReference(Guid id, Guid referenceId, CancellationToken ct)
        => Ok(await _stories.ApproveReferenceAsync(RequireTenantId(), id, referenceId, ct));

    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<StoryProjectDto>>> ListProjects(CancellationToken ct)
        => Ok(await _stories.ListProjectsAsync(RequireTenantId(), ct));

    [HttpPost("projects")]
    public async Task<ActionResult<StoryProjectDto>> CreateProject([FromBody] CreateStoryProjectRequest request, CancellationToken ct)
        => Ok(await _stories.CreateProjectAsync(RequireTenantId(), _currentUser.UserId, request, ct));

    [HttpGet("projects/{id:guid}")]
    public async Task<ActionResult<StoryProjectDto>> GetProject(Guid id, CancellationToken ct)
        => Ok(await _stories.GetProjectAsync(RequireTenantId(), id, ct));

    [HttpPut("projects/{id:guid}/video-provider")]
    public async Task<ActionResult<StoryProjectDto>> SetVideoProvider(Guid id, [FromBody] SetVideoProviderRequest request, CancellationToken ct)
        => Ok(await _stories.SetVideoProviderAsync(RequireTenantId(), id, request.Provider, ct));

    [HttpGet("projects/{id:guid}/cost-estimate")]
    public async Task<ActionResult<StoryCostEstimateDto>> EstimateCost(Guid id, CancellationToken ct)
        => Ok(await _stories.EstimateCostAsync(RequireTenantId(), id, ct));

    [HttpGet("projects/{id:guid}/script")]
    public async Task<ActionResult<StoryScriptVersionDto>> GetScript(Guid id, CancellationToken ct)
    {
        var script = await _stories.GetCurrentScriptAsync(RequireTenantId(), id, ct);
        return script is null ? NotFound() : Ok(script);
    }

    [HttpPost("projects/{id:guid}/script/generate")]
    public async Task<ActionResult<StoryScriptVersionDto>> GenerateScript(Guid id, CancellationToken ct)
        => Ok(await _stories.GenerateScriptAsync(RequireTenantId(), _currentUser.UserId, id, ct));

    [HttpPut("projects/{id:guid}/script")]
    public async Task<ActionResult<StoryScriptVersionDto>> SaveScript(Guid id, [FromBody] SaveStoryScriptRequest request, CancellationToken ct)
        => Ok(await _stories.SaveScriptAsync(RequireTenantId(), id, request, ct));

    [HttpPost("projects/{id:guid}/script/approve")]
    public async Task<ActionResult<IReadOnlyList<StorySceneDto>>> ApproveScript(Guid id, CancellationToken ct)
        => Ok(await _stories.ApproveScriptAsync(RequireTenantId(), _currentUser.UserId, id, ct));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content studio request has no tenant in scope.");
}

public sealed record SetVideoProviderRequest(SceneVideoProviderKind Provider);
