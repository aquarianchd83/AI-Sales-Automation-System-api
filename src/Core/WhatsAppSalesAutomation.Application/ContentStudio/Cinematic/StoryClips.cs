using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record StoryClipSceneDto(
    Guid Id, int Index, int TargetSeconds, StorySceneStatus Status, string VideoPrompt, IReadOnlyList<StoryDialogueDto> Dialogue,
    int? ActualMilliseconds, string? Error, string? FlagReason, decimal? CostUsd);

public sealed record GenerateClipsRequest(bool ConfirmCost);

/// <summary>Enqueues the background step for a job. The tenant crosses the boundary explicitly: the job runs in its own scope.</summary>
public interface IStoryClipQueue
{
    void Enqueue(Guid jobId, Guid tenantId, TimeSpan? delay = null);
}

public interface IStoryClipService
{
    Task<IReadOnlyList<StoryClipSceneDto>> ListScenesAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);

    /// <summary>Starts every scene that has no finished clip. A paid provider needs <c>ConfirmCost</c>; a project budget is enforced here.</summary>
    Task<IReadOnlyList<StoryClipSceneDto>> GenerateClipsAsync(Guid tenantId, Guid? userId, Guid projectId, GenerateClipsRequest request, CancellationToken ct = default);

    /// <summary>Regenerates one scene only. Confirmed separately because it costs again.</summary>
    Task<StoryClipSceneDto> RegenerateSceneAsync(Guid tenantId, Guid? userId, Guid sceneId, GenerateClipsRequest request, CancellationToken ct = default);

    /// <summary>Flow import, or replacing a clip by hand: the user's own file for one scene.</summary>
    Task<StoryClipSceneDto> UploadClipAsync(Guid tenantId, Guid? userId, Guid sceneId, Stream content, string fileName, CancellationToken ct = default);

    /// <summary>Marks a finished clip as not good enough (bad lip movement, wrong face), so only it can be redone.</summary>
    Task<StoryClipSceneDto> FlagSceneAsync(Guid tenantId, Guid sceneId, string reason, CancellationToken ct = default);
}

public interface IStoryClipRunner
{
    /// <summary>One step of one job: submit, or poll, or finish. Records its own failures; never throws for a provider problem.</summary>
    Task RunAsync(Guid jobId, CancellationToken ct = default);
}

public class StoryClipService : IStoryClipService, IStoryClipRunner
{
    public const long MaxClipBytes = 200L * 1024 * 1024;
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(20);

    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly ISceneVideoProviderFactory _providers;
    private readonly IStudioMeteringService _metering;
    private readonly ITenantDocumentStore _store;
    private readonly IFfmpegService _ffmpeg;
    private readonly IStoryClipQueue _queue;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<StoryClipService> _logger;

    public StoryClipService(
        IApplicationDbContext context, ITenantConfigOverrideProvider config, ISceneVideoProviderFactory providers, IStudioMeteringService metering,
        ITenantDocumentStore store, IFfmpegService ffmpeg, IStoryClipQueue queue, IDateTimeProvider clock, ILogger<StoryClipService> logger)
    {
        _context = context;
        _config = config;
        _providers = providers;
        _metering = metering;
        _store = store;
        _ffmpeg = ffmpeg;
        _queue = queue;
        _clock = clock;
        _logger = logger;
    }

    // ----- the user's side ---------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StoryClipSceneDto>> ListScenesAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        return await SceneDtosAsync(project, ct);
    }

    public async Task<IReadOnlyList<StoryClipSceneDto>> GenerateClipsAsync(
        Guid tenantId, Guid? userId, Guid projectId, GenerateClipsRequest request, CancellationToken ct = default)
    {
        var project = await ProjectAsync(tenantId, projectId, ct);
        if (project.Status is StoryProjectStatus.Draft) throw new ConflictException("Approve the script before making clips.");
        if (project.Status is StoryProjectStatus.Cancelled) throw new ConflictException("This story was cancelled.");

        var todo = await ScenesOfCurrentScriptAsync(project, ct);
        todo = todo.Where(s => s.Status is StorySceneStatus.Pending or StorySceneStatus.Failed or StorySceneStatus.Flagged).ToList();
        if (todo.Count == 0) throw new ConflictException("Every scene already has a clip or is being made.");

        await EnsureCostAllowedAsync(project, todo, request.ConfirmCost, ct);
        foreach (var scene in todo) await StartSceneAsync(project, scene, userId, ct);

        project.Status = StoryProjectStatus.Generating;
        await _context.SaveChangesAsync(ct);
        return await SceneDtosAsync(project, ct);
    }

    public async Task<StoryClipSceneDto> RegenerateSceneAsync(Guid tenantId, Guid? userId, Guid sceneId, GenerateClipsRequest request, CancellationToken ct = default)
    {
        var scene = await SceneAsync(tenantId, sceneId, ct);
        var project = await ProjectAsync(tenantId, scene.ProjectId, ct);
        if (scene.Status is StorySceneStatus.Queued or StorySceneStatus.Processing)
            throw new ConflictException("This scene is already being made.");

        await EnsureCostAllowedAsync(project, new List<StoryScene> { scene }, request.ConfirmCost, ct);
        await StartSceneAsync(project, scene, userId, ct);
        project.Status = StoryProjectStatus.Generating;
        await _context.SaveChangesAsync(ct);
        return (await SceneDtosAsync(project, ct)).First(s => s.Id == sceneId);
    }

    public async Task<StoryClipSceneDto> UploadClipAsync(Guid tenantId, Guid? userId, Guid sceneId, Stream content, string fileName, CancellationToken ct = default)
    {
        var scene = await SceneAsync(tenantId, sceneId, ct);
        var project = await ProjectAsync(tenantId, scene.ProjectId, ct);
        if (project.Status is StoryProjectStatus.Draft) throw new ConflictException("Approve the script before adding clips.");
        if (scene.Status is StorySceneStatus.Queued or StorySceneStatus.Processing)
            throw new ConflictException("This scene is being made automatically; wait for it to finish or flag it first.");
        if (!_store.IsConfigured) throw new StorageUnavailableException("File storage is not set up yet, so clips cannot be uploaded.");

        var temp = Path.Combine(Path.GetTempPath(), $"upload-{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxClipBytes) throw new ArgumentException($"The clip is larger than {MaxClipBytes / 1024 / 1024} MB.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            VideoProbe probe;
            try { probe = await _ffmpeg.ProbeAsync(temp, ct); }
            catch (InvalidOperationException) { throw new ArgumentException("That file is not a video the studio can read. Upload an MP4."); }
            if (probe.Height < probe.Width) throw new ArgumentException($"The clip is {probe.Width}x{probe.Height} (landscape). Generate it vertically (9:16).");
            if (probe.DurationSeconds < 1) throw new ArgumentException("The clip is too short.");

            // The job row is the record of where the clip came from: an upload for this scene, not a provider call.
            var job = await LatestJobAsync(scene.Id, ct);
            if (job is not { Status: StoryJobStatus.Processing, Provider: nameof(SceneVideoProviderKind.FlowImport) })
            {
                job = NewJob(project, scene, SceneVideoProviderKind.FlowImport, await NextAttemptAsync(scene.Id, ct), 0m);
                _context.StoryGenerationJobs.Add(job);
            }

            await FinishClipAsync(project, scene, job, temp, 0m, ct);
        }
        finally
        {
            TryDelete(temp);
        }

        return (await SceneDtosAsync(project, ct)).First(s => s.Id == sceneId);
    }

    public async Task<StoryClipSceneDto> FlagSceneAsync(Guid tenantId, Guid sceneId, string reason, CancellationToken ct = default)
    {
        var scene = await SceneAsync(tenantId, sceneId, ct);
        var project = await ProjectAsync(tenantId, scene.ProjectId, ct);
        if (scene.Status is not (StorySceneStatus.Completed or StorySceneStatus.Failed))
            throw new ConflictException("Only a finished clip can be flagged.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say what is wrong with the clip.");

        scene.Status = StorySceneStatus.Flagged;
        scene.FlagReason = reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
        // The flagged clip's file stays in storage: replacing a clip never deletes the original.
        if (project.Status is StoryProjectStatus.InReview) project.Status = StoryProjectStatus.ScriptApproved;
        await _context.SaveChangesAsync(ct);
        return (await SceneDtosAsync(project, ct)).First(s => s.Id == sceneId);
    }

    // ----- the background step ----------------------------------------------------------------------------------------------------

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _context.StoryGenerationJobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is StoryJobStatus.Completed or StoryJobStatus.Cancelled or StoryJobStatus.Failed) return;

        var scene = await _context.StoryScenes.IgnoreQueryFilters().FirstAsync(s => s.Id == job.SceneId, ct);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == job.ProjectId, ct);
        var provider = _providers.For(project.VideoProvider);

        try
        {
            job.Status = StoryJobStatus.Processing;
            job.StartedAtUtc ??= _clock.UtcNow;
            scene.Status = StorySceneStatus.Processing;

            SceneClipResult result;
            if (job.ProviderJobId is null)
            {
                job.Attempts++;
                var refs = await ReferenceKeysAsync(project.Id, ct);
                result = await provider.SubmitAsync(new SceneClipRequest(scene.Id, scene.VideoPrompt, scene.TargetSeconds, "9:16", refs, job.IdempotencyKey), ct);
                // Saved BEFORE polling: after a restart the poll resumes from this id instead of paying for a second generation.
                job.ProviderJobId = result.ProviderJobId;
                await _context.SaveChangesAsync(ct);
            }
            else
            {
                result = await provider.PollAsync(job.ProviderJobId, ct);
            }

            switch (result.State)
            {
                case SceneClipState.Running:
                    if (_clock.UtcNow - (job.StartedAtUtc ?? _clock.UtcNow) > GiveUpAfter)
                        await FailAsync(project, scene, job, "The provider took too long. The clip may still be billed by the provider.", ct);
                    else
                    {
                        await _context.SaveChangesAsync(ct);
                        _queue.Enqueue(job.Id, job.TenantId, PollEvery);
                    }

                    break;
                case SceneClipState.Completed:
                    await FinishClipAsync(project, scene, job, result.ClipPath!, result.CostUsd, ct);
                    break;
                case SceneClipState.AwaitingImport:
                    scene.Status = StorySceneStatus.AwaitingImport;
                    await _context.SaveChangesAsync(ct);
                    break;
                default:
                    await FailAsync(project, scene, job, result.Error ?? "The provider could not make the clip.", ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Story clip job {JobId} failed", jobId);
            await FailAsync(project, scene, job, "Something went wrong while making the clip: " + ex.Message, ct);
        }
    }

    // ----- shared pieces ----------------------------------------------------------------------------------------------------------

    private async Task StartSceneAsync(StoryProject project, StoryScene scene, Guid? userId, CancellationToken ct)
    {
        var provider = _providers.For(project.VideoProvider);
        var estimate = provider.EstimateCostUsd(scene.TargetSeconds) ?? 0m;
        var attempt = await NextAttemptAsync(scene.Id, ct);
        var job = NewJob(project, scene, project.VideoProvider, attempt, estimate);
        _context.StoryGenerationJobs.Add(job);
        scene.FlagReason = null;

        if (project.VideoProvider == SceneVideoProviderKind.FlowImport)
        {
            // Nothing runs: the user generates the clip in Flow and uploads it.
            job.Status = StoryJobStatus.Processing;
            scene.Status = StorySceneStatus.AwaitingImport;
            return;
        }

        if (estimate > 0)
        {
            var charge = await _metering.ChargeAsync(new StudioChargeRequest(
                project.TenantId, StudioActivityKind.StorySceneClip, job.IdempotencyKey, $"Scene {scene.Index} clip of '{project.Title}'",
                ReferenceType: nameof(StoryScene), ReferenceId: scene.Id.ToString(), Provider: project.VideoProvider.ToString(), ActorUserId: userId), ct);
            if (!charge.Allowed) throw new PlanLimitExceededException(charge.Message ?? "Your video quota is used up.");
            job.ChargeActivityId = charge.ActivityId; // remembered so a failure can give the units back
        }

        scene.Status = StorySceneStatus.Queued;
        await _context.SaveChangesAsync(ct);
        _queue.Enqueue(job.Id, project.TenantId);
    }

    private async Task EnsureCostAllowedAsync(StoryProject project, IReadOnlyList<StoryScene> scenes, bool confirmed, CancellationToken ct)
    {
        var provider = _providers.For(project.VideoProvider);
        var estimate = scenes.Sum(s => provider.EstimateCostUsd(s.TargetSeconds) ?? 0m);
        if (estimate <= 0) return;

        if (!confirmed) throw new ArgumentException($"This will cost about {estimate:0.00} USD. Confirm the cost to continue.");

        if (project.BudgetUsd is { } budget)
        {
            // Everything already tried counts, including failed attempts: the provider may have billed them.
            var spent = await _context.StoryGenerationJobs.IgnoreQueryFilters()
                .Where(j => j.ProjectId == project.Id).SumAsync(j => j.ActualCostUsd ?? j.EstimatedCostUsd ?? 0m, ct);
            if (spent + estimate > budget)
                throw new ConflictException($"This would bring the story to {spent + estimate:0.00} USD, over its {budget:0.00} USD budget.");
        }
    }

    private async Task FinishClipAsync(StoryProject project, StoryScene scene, StoryGenerationJob job, string localPath, decimal? cost, CancellationToken ct)
    {
        try
        {
            var probe = await _ffmpeg.ProbeAsync(localPath, ct);
            var key = await StoreAsync(project, scene, localPath, ct);
            var asset = new StoryMediaAsset
            {
                TenantId = project.TenantId,
                ProjectId = project.Id,
                Kind = StoryMediaKind.SceneClip,
                StorageKey = key,
                ContentType = "video/mp4",
                SizeBytes = new FileInfo(localPath).Length,
                DurationMs = (int)(probe.DurationSeconds * 1000),
                Width = probe.Width,
                Height = probe.Height,
                FramesPerSecond = probe.FramesPerSecond
            };
            _context.StoryMediaAssets.Add(asset);

            scene.ClipAssetId = asset.Id;
            scene.ActualMilliseconds = asset.DurationMs;
            scene.Status = StorySceneStatus.Completed;
            job.Status = StoryJobStatus.Completed;
            job.FinishedAtUtc = _clock.UtcNow;
            job.ActualCostUsd = cost ?? job.EstimatedCostUsd;
            job.Error = null;
            await CompleteChargeAsync(job, ct);

            var all = await ScenesOfCurrentScriptAsync(project, ct);
            if (all.All(s => s.Status == StorySceneStatus.Completed)) project.Status = StoryProjectStatus.InReview;
            await _context.SaveChangesAsync(ct);
        }
        finally
        {
            // Provider downloads are temporary files of ours; an upload's temp is deleted by its caller.
            if (Path.GetFileName(localPath).StartsWith("veo-", StringComparison.Ordinal) || Path.GetFileName(localPath).StartsWith("sim-", StringComparison.Ordinal)) TryDelete(localPath);
        }
    }

    private async Task<string> StoreAsync(StoryProject project, StoryScene scene, string localPath, CancellationToken ct)
    {
        // A new object per attempt: the original clip is kept when a scene is redone.
        await using var stream = File.OpenRead(localPath);
        return await _store.SaveAsync(project.TenantId, $"stories/{project.Id}/scene-{scene.Index}-{Guid.NewGuid():N}.mp4", stream, "video/mp4", ct);
    }

    private async Task FailAsync(StoryProject project, StoryScene scene, StoryGenerationJob job, string error, CancellationToken ct)
    {
        job.Status = StoryJobStatus.Failed;
        job.FinishedAtUtc = _clock.UtcNow;
        scene.Status = StorySceneStatus.Failed;
        job.Error = error.Length <= 2000 ? error : error[..2000];
        await RefundChargeAsync(job, error, ct);

        var all = await ScenesOfCurrentScriptAsync(project, ct);
        if (!all.Any(s => s.Status is StorySceneStatus.Queued or StorySceneStatus.Processing or StorySceneStatus.AwaitingImport))
            project.Status = StoryProjectStatus.Failed;
        await _context.SaveChangesAsync(ct);
    }

    private Task CompleteChargeAsync(StoryGenerationJob job, CancellationToken ct) =>
        job.ChargeActivityId is { } activity ? _metering.CompleteAsync(activity, ct) : Task.CompletedTask;

    private Task RefundChargeAsync(StoryGenerationJob job, string reason, CancellationToken ct) =>
        job.ChargeActivityId is { } activity ? _metering.RefundAsync(activity, reason, ct) : Task.CompletedTask;

    private StoryGenerationJob NewJob(StoryProject project, StoryScene scene, SceneVideoProviderKind kind, int attempt, decimal estimate) => new()
    {
        TenantId = project.TenantId,
        ProjectId = project.Id,
        SceneId = scene.Id,
        Kind = StoryJobKind.SceneClip,
        Status = StoryJobStatus.Queued,
        Provider = kind.ToString(),
        IdempotencyKey = $"clip:{scene.Id:N}:{attempt}",
        EstimatedCostUsd = estimate
    };

    private async Task<int> NextAttemptAsync(Guid sceneId, CancellationToken ct) =>
        await _context.StoryGenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SceneId == sceneId, ct) + 1;

    private Task<StoryGenerationJob?> LatestJobAsync(Guid sceneId, CancellationToken ct) =>
        _context.StoryGenerationJobs.IgnoreQueryFilters().Where(j => j.SceneId == sceneId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyList<string>> ReferenceKeysAsync(Guid projectId, CancellationToken ct)
    {
        var ids = _context.StoryProjectCharacters.IgnoreQueryFilters().Where(l => l.ProjectId == projectId).Select(l => l.CharacterId);
        return await _context.StoryCharacterReferences.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.CharacterId) && r.Status == StoryReferenceStatus.Approved).Select(r => r.StorageKey).ToListAsync(ct);
    }

    private async Task<List<StoryScene>> ScenesOfCurrentScriptAsync(StoryProject project, CancellationToken ct) =>
        await _context.StoryScenes.IgnoreQueryFilters()
            .Where(s => s.ProjectId == project.Id && s.ScriptVersionId == project.CurrentScriptVersionId).OrderBy(s => s.Index).ToListAsync(ct);

    private async Task<IReadOnlyList<StoryClipSceneDto>> SceneDtosAsync(StoryProject project, CancellationToken ct)
    {
        var scenes = await ScenesOfCurrentScriptAsync(project, ct);
        var ids = scenes.Select(s => s.Id).ToList();
        var lines = await _context.StoryDialogueLines.IgnoreQueryFilters().Where(l => ids.Contains(l.SceneId)).OrderBy(l => l.Order).ToListAsync(ct);
        var jobs = await _context.StoryGenerationJobs.IgnoreQueryFilters().Where(j => j.SceneId != null && ids.Contains(j.SceneId.Value)).ToListAsync(ct);

        return scenes.Select(s =>
        {
            var latest = jobs.Where(j => j.SceneId == s.Id).OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.IdempotencyKey).FirstOrDefault();
            var failure = latest is { Status: StoryJobStatus.Failed } ? latest.Error : null;
            return new StoryClipSceneDto(
                s.Id, s.Index, s.TargetSeconds, s.Status, s.VideoPrompt,
                lines.Where(l => l.SceneId == s.Id).Select(l => new StoryDialogueDto(l.SpeakerName, l.Text, l.StartMs, l.EndMs)).ToList(),
                s.ActualMilliseconds, failure, s.FlagReason, jobs.Where(j => j.SceneId == s.Id).Sum(j => j.ActualCostUsd ?? 0m));
        }).ToList();
    }

    private async Task<StoryProject> ProjectAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        return await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);
    }

    private async Task<StoryScene> SceneAsync(Guid tenantId, Guid sceneId, CancellationToken ct) =>
        await _context.StoryScenes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == sceneId && s.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Scene", sceneId);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a temp file left behind is harmless */ }
    }
}
