using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record StoryUsageLineDto(string Category, int Jobs, int Failed, decimal EstimatedUsd, decimal ActualUsd);

public sealed record StoryUsageDto(IReadOnlyList<StoryUsageLineDto> Lines, decimal TotalUsd, decimal? BudgetUsd, decimal WastedOnFailedUsd);

public sealed record EditDialogueRequest(string Text);

public interface IStoryReviewService
{
    /// <summary>Corrects the subtitle text of one dialogue line. Only the subtitles change: the clip's audio does not, so the approved script is untouched.</summary>
    Task<StoryClipSceneDto> EditDialogueAsync(Guid tenantId, Guid sceneId, int order, string text, CancellationToken ct = default);

    /// <summary>The final approval before anything is published. Needs a finished render of the approved script made after the last subtitle correction.</summary>
    Task<StoryProjectDto> ApproveFinalAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default);

    /// <summary>What the story has cost, by kind of work, including attempts that failed (the provider may still have billed them).</summary>
    Task<StoryUsageDto> GetUsageAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);
}

public class StoryReviewService : IStoryReviewService
{
    public const int MaxSubtitleChars = 200;

    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;

    public StoryReviewService(IApplicationDbContext context, ITenantConfigOverrideProvider config)
    {
        _context = context;
        _config = config;
    }

    public async Task<StoryClipSceneDto> EditDialogueAsync(Guid tenantId, Guid sceneId, int order, string text, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var scene = await _context.StoryScenes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == sceneId && s.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Scene", sceneId);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == scene.ProjectId, ct);
        if (project.Status is StoryProjectStatus.Draft or StoryProjectStatus.Approved or StoryProjectStatus.Cancelled)
            throw new ConflictException("The subtitles can only be corrected after the script is approved and before the video is approved.");

        var clean = (text ?? string.Empty).Trim();
        if (clean.Length is 0 or > MaxSubtitleChars) throw new ArgumentException($"A subtitle needs 1 to {MaxSubtitleChars} characters.");

        var line = await _context.StoryDialogueLines.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.SceneId == sceneId && l.Order == order, ct)
            ?? throw new NotFoundException("Dialogue line", order);
        line.Text = clean;
        line.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        var lines = await _context.StoryDialogueLines.IgnoreQueryFilters().Where(l => l.SceneId == sceneId).OrderBy(l => l.Order).ToListAsync(ct);
        return new StoryClipSceneDto(scene.Id, scene.Index, scene.TargetSeconds, scene.Status, scene.VideoPrompt,
            lines.Select(l => new StoryDialogueDto(l.SpeakerName, l.Text, l.StartMs, l.EndMs)).ToList(), scene.ActualMilliseconds, null, scene.FlagReason, 0m);
    }

    public async Task<StoryProjectDto> ApproveFinalAsync(Guid tenantId, Guid? userId, Guid projectId, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);
        if (project.Status != StoryProjectStatus.InReview) throw new ConflictException("The video can only be approved once every scene has a clip and it has been put together.");

        var render = await _context.StoryRenders.IgnoreQueryFilters().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (render is not { Status: StoryJobStatus.Completed }) throw new ConflictException("Put the video together and watch it before approving.");

        var version = await _context.StoryScriptVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == project.CurrentScriptVersionId, ct);
        if (render.ContentHash != version.ContentHash) throw new ConflictException("This video was made from a different script. Put it together again.");

        var sceneIds = _context.StoryScenes.IgnoreQueryFilters().Where(s => s.ProjectId == projectId && s.ScriptVersionId == version.Id).Select(s => s.Id);
        var changedAfter = await _context.StoryDialogueLines.IgnoreQueryFilters().AnyAsync(l => sceneIds.Contains(l.SceneId) && l.UpdatedAt != null && l.UpdatedAt > render.CreatedAt, ct);
        if (changedAfter) throw new ConflictException("The subtitles were corrected after this video was made. Put it together again so the corrections appear.");

        project.Status = StoryProjectStatus.Approved;
        await _context.SaveChangesAsync(ct);
        var cast = await _context.StoryProjectCharacters.IgnoreQueryFilters().Where(l => l.ProjectId == projectId).Select(l => l.CharacterId).ToListAsync(ct);
        return new StoryProjectDto(project.Id, project.Title, project.Topic, project.Language, project.Style, project.Audience, project.Moral, project.TargetSeconds,
            project.Status, project.VideoProvider, project.BudgetUsd, project.CurrentScriptVersionId, cast, project.CreatedAt);
    }

    public async Task<StoryUsageDto> GetUsageAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);

        var jobs = await _context.StoryGenerationJobs.IgnoreQueryFilters().Where(j => j.ProjectId == projectId).ToListAsync(ct);
        var lines = jobs.GroupBy(j => j.Kind).OrderBy(g => g.Key).Select(g => new StoryUsageLineDto(
            Label(g.Key), g.Count(), g.Count(j => j.Status == StoryJobStatus.Failed),
            g.Sum(j => j.EstimatedCostUsd ?? 0m), g.Sum(j => j.ActualCostUsd ?? 0m))).ToList();

        // A failed attempt may still have been billed by the provider; it is shown on its own so it is not mistaken for a saving.
        var wasted = jobs.Where(j => j.Status == StoryJobStatus.Failed).Sum(j => j.ActualCostUsd ?? j.EstimatedCostUsd ?? 0m);
        return new StoryUsageDto(lines, jobs.Sum(j => j.ActualCostUsd ?? j.EstimatedCostUsd ?? 0m), project.BudgetUsd, wasted);
    }

    private static string Label(StoryJobKind kind) => kind switch
    {
        StoryJobKind.Script => "Script",
        StoryJobKind.CharacterImage => "Character pictures",
        StoryJobKind.SceneClip => "Scene clips",
        StoryJobKind.Speech => "Speech",
        StoryJobKind.LipSync => "Lip-sync",
        StoryJobKind.Render => "Final video",
        _ => kind.ToString()
    };
}
