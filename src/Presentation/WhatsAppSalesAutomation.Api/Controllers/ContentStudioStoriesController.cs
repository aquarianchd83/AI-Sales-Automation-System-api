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
    private readonly IStoryClipService _clips;
    private readonly IStoryRenderService _renders;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserService _currentUser;

    public ContentStudioStoriesController(IStoryStudioService stories, IStoryClipService clips, IStoryRenderService renders, ITenantContext tenantContext, ICurrentUserService currentUser)
    {
        _stories = stories;
        _clips = clips;
        _renders = renders;
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

    [HttpGet("projects/{id:guid}/scenes")]
    public async Task<ActionResult<IReadOnlyList<StoryClipSceneDto>>> ListScenes(Guid id, CancellationToken ct)
        => Ok(await _clips.ListScenesAsync(RequireTenantId(), id, ct));

    /// <summary>Starts every scene without a finished clip. A paid provider needs <c>confirmCost</c>.</summary>
    [HttpPost("projects/{id:guid}/clips/generate")]
    public async Task<ActionResult<IReadOnlyList<StoryClipSceneDto>>> GenerateClips(Guid id, [FromBody] GenerateClipsRequest request, CancellationToken ct)
        => Ok(await _clips.GenerateClipsAsync(RequireTenantId(), _currentUser.UserId, id, request, ct));

    [HttpPost("scenes/{sceneId:guid}/regenerate")]
    public async Task<ActionResult<StoryClipSceneDto>> RegenerateScene(Guid sceneId, [FromBody] GenerateClipsRequest request, CancellationToken ct)
        => Ok(await _clips.RegenerateSceneAsync(RequireTenantId(), _currentUser.UserId, sceneId, request, ct));

    /// <summary>The Flow path: the user generated the clip in Flow and uploads it for this scene.</summary>
    [HttpPost("scenes/{sceneId:guid}/clip")]
    [RequestSizeLimit(StoryClipService.MaxClipBytes + 1024 * 1024)]
    public async Task<ActionResult<StoryClipSceneDto>> UploadClip(Guid sceneId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose a video to upload." });
        await using var stream = file.OpenReadStream();
        return Ok(await _clips.UploadClipAsync(RequireTenantId(), _currentUser.UserId, sceneId, stream, file.FileName, ct));
    }

    [HttpPost("scenes/{sceneId:guid}/flag")]
    public async Task<ActionResult<StoryClipSceneDto>> FlagScene(Guid sceneId, [FromBody] FlagSceneRequest request, CancellationToken ct)
        => Ok(await _clips.FlagSceneAsync(RequireTenantId(), sceneId, request.Reason, ct));

    [HttpGet("projects/{id:guid}/renders")]
    public async Task<ActionResult<IReadOnlyList<StoryRenderDto>>> ListRenders(Guid id, CancellationToken ct)
        => Ok(await _renders.ListRendersAsync(RequireTenantId(), id, ct));

    /// <summary>Puts the clips together with subtitles into the final video. Runs in the background.</summary>
    [HttpPost("projects/{id:guid}/renders")]
    public async Task<ActionResult<StoryRenderDto>> StartRender(Guid id, CancellationToken ct)
        => Ok(await _renders.StartRenderAsync(RequireTenantId(), _currentUser.UserId, id, ct));

    [HttpGet("renders/{renderId:guid}/video")]
    public async Task<IActionResult> DownloadFinal(Guid renderId, CancellationToken ct)
    {
        var stream = await _renders.OpenFinalAsync(RequireTenantId(), renderId, ct);
        return stream is null ? NotFound() : File(stream, "video/mp4", $"story-{renderId:N}.mp4", enableRangeProcessing: true);
    }

    [HttpGet("scenes/{sceneId:guid}/clip")]
    public async Task<IActionResult> PreviewClip(Guid sceneId, CancellationToken ct)
    {
        var stream = await _renders.OpenClipAsync(RequireTenantId(), sceneId, ct);
        return stream is null ? NotFound() : File(stream, "video/mp4", enableRangeProcessing: true);
    }

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content studio request has no tenant in scope.");
}

public sealed record SetVideoProviderRequest(SceneVideoProviderKind Provider);

public sealed record FlagSceneRequest(string Reason);
