using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record SpeechRequest(string Text, string Language, string? VoiceHint, string CharacterName);

/// <summary>Speech as an audio file on disk (the caller deletes it), or an error. Providers rarely return exactly the time a line was given.</summary>
public sealed record SpeechResult(bool Success, string? AudioPath, decimal? CostUsd, string? Error);

public interface ISpeechProvider
{
    string Name { get; }

    decimal? EstimateCostUsd();

    Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}

public sealed record VoiceOverRequest(bool ConfirmCost);

public sealed record VoiceOverResultDto(StoryClipSceneDto Scene, IReadOnlyList<string> Warnings, string Provider, decimal CostUsd);

public interface IStoryVoiceService
{
    /// <summary>
    /// Replaces a finished clip's own audio with generated speech: one voice per character, each line placed at its own time, so only the speaker is heard.
    /// The original clip is kept. Lines whose speech runs longer than the time they were given are reported, not squeezed.
    /// </summary>
    Task<VoiceOverResultDto> ApplyVoiceOverAsync(Guid tenantId, Guid? userId, Guid sceneId, VoiceOverRequest request, CancellationToken ct = default);
}

public class StoryVoiceService : IStoryVoiceService
{
    public const int OverrunToleranceMs = 600;

    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly ISpeechProvider _speech;
    private readonly IFfmpegService _ffmpeg;
    private readonly ITenantDocumentStore _store;
    private readonly IStudioMeteringService _metering;
    private readonly IStoryClipService _clips;

    public StoryVoiceService(
        IApplicationDbContext context, ITenantConfigOverrideProvider config, ISpeechProvider speech, IFfmpegService ffmpeg, ITenantDocumentStore store,
        IStudioMeteringService metering, IStoryClipService clips)
    {
        _context = context;
        _config = config;
        _speech = speech;
        _ffmpeg = ffmpeg;
        _store = store;
        _metering = metering;
        _clips = clips;
    }

    public async Task<VoiceOverResultDto> ApplyVoiceOverAsync(Guid tenantId, Guid? userId, Guid sceneId, VoiceOverRequest request, CancellationToken ct = default)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var scene = await _context.StoryScenes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == sceneId && s.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Scene", sceneId);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == scene.ProjectId, ct);
        if (scene.Status != StorySceneStatus.Completed || scene.ClipAssetId is null) throw new ConflictException("The scene needs a finished clip first.");
        if (project.Status is StoryProjectStatus.Approved or StoryProjectStatus.Cancelled) throw new ConflictException("The video is already approved.");
        if (!_store.IsConfigured) throw new StorageUnavailableException("File storage is not set up yet.");

        var lines = await _context.StoryDialogueLines.IgnoreQueryFilters().Where(l => l.SceneId == sceneId).OrderBy(l => l.Order).ToListAsync(ct);
        if (lines.Count == 0) throw new ConflictException("This scene has no dialogue to speak.");

        var each = _speech.EstimateCostUsd() ?? 0m;
        var estimate = each * lines.Count;
        if (estimate > 0 && !request.ConfirmCost) throw new ArgumentException($"This will cost about {estimate:0.00} USD. Confirm the cost to continue.");

        var characterIds = lines.Where(l => l.CharacterId != null).Select(l => l.CharacterId!.Value).Distinct().ToList();
        var voices = await _context.StoryCharacters.IgnoreQueryFilters().Where(c => characterIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.VoicePreferences, ct);

        Guid? activity = null;
        if (estimate > 0)
        {
            var charge = await _metering.ChargeAsync(new StudioChargeRequest(
                tenantId, StudioActivityKind.StoryVoice, $"voice:{sceneId:N}:{Guid.NewGuid():N}", $"Voice-over for scene {scene.Index} of '{project.Title}'",
                ReferenceType: nameof(StoryScene), ReferenceId: sceneId.ToString(), Provider: _speech.Name, ActorUserId: userId), ct);
            if (!charge.Allowed) throw new PlanLimitExceededException(charge.Message ?? "Your content quota is used up.");
            activity = charge.ActivityId;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"story-voice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var warnings = new List<string>();
        var job = new StoryGenerationJob
        {
            TenantId = tenantId, ProjectId = project.Id, SceneId = sceneId, Kind = StoryJobKind.Speech, Provider = _speech.Name, Status = StoryJobStatus.Processing,
            IdempotencyKey = $"voice:{sceneId:N}:{Guid.NewGuid():N}", EstimatedCostUsd = estimate, ChargeActivityId = activity, StartedAtUtc = DateTime.UtcNow
        };
        _context.StoryGenerationJobs.Add(job);

        try
        {
            var spoken = new List<(string AudioPath, int StartMs)>();
            decimal cost = 0m;
            foreach (var line in lines)
            {
                var voice = line.CharacterId is { } id && voices.TryGetValue(id, out var hint) ? hint : null;
                var result = await _speech.SynthesizeAsync(new SpeechRequest(line.Text, project.Language, voice, line.SpeakerName), ct);
                if (!result.Success || result.AudioPath is null) throw new InvalidOperationException(result.Error ?? "The speech could not be made.");
                spoken.Add((result.AudioPath, line.StartMs));
                cost += result.CostUsd ?? each;

                var length = (await _ffmpeg.ProbeAudioMillisecondsAsync(result.AudioPath, ct));
                if (length > (line.EndMs - line.StartMs) + OverrunToleranceMs)
                    warnings.Add($"{line.SpeakerName}'s line \"{Short(line.Text)}\" takes {length / 1000.0:0.0}s but was given {(line.EndMs - line.StartMs) / 1000.0:0.0}s; it may run into the next line.");
            }

            await using var source = await OpenAsync(scene.ClipAssetId.Value, ct);
            var clipPath = Path.Combine(dir, "clip.mp4");
            await using (var file = File.Create(clipPath)) await source.CopyToAsync(file, ct);

            var output = Path.Combine(dir, "voiced.mp4");
            await _ffmpeg.ReplaceAudioAsync(clipPath, spoken, output, ct);
            var probe = await _ffmpeg.ProbeAsync(output, ct);

            await using var voiced = File.OpenRead(output);
            var key = await _store.SaveAsync(tenantId, $"stories/{project.Id}/scene-{scene.Index}-voice-{Guid.NewGuid():N}.mp4", voiced, "video/mp4", ct);
            var asset = new StoryMediaAsset
            {
                TenantId = tenantId, ProjectId = project.Id, Kind = StoryMediaKind.SceneClip, StorageKey = key, ContentType = "video/mp4", SizeBytes = new FileInfo(output).Length,
                DurationMs = (int)(probe.DurationSeconds * 1000), Width = probe.Width, Height = probe.Height, FramesPerSecond = probe.FramesPerSecond, IsOriginal = false
            };
            _context.StoryMediaAssets.Add(asset);
            scene.ClipAssetId = asset.Id;
            scene.ActualMilliseconds = asset.DurationMs;
            scene.VoiceOverApplied = true;
            job.Status = StoryJobStatus.Completed;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.ActualCostUsd = cost;
            await _context.SaveChangesAsync(ct);
            if (activity is { } done) await _metering.CompleteAsync(done, ct);

            var dto = (await _clips.ListScenesAsync(tenantId, project.Id, ct)).First(s => s.Id == sceneId);
            return new VoiceOverResultDto(dto, warnings, _speech.Name, cost);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Status = StoryJobStatus.Failed;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.Error = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
            await _context.SaveChangesAsync(CancellationToken.None);
            if (activity is { } failed) await _metering.RefundAsync(failed, ex.Message, CancellationToken.None);
            throw new InvalidOperationException("The voice-over could not be made and the clip is unchanged: " + ex.Message, ex);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* harmless */ }
        }
    }

    private async Task<Stream> OpenAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await _context.StoryMediaAssets.IgnoreQueryFilters().FirstAsync(a => a.Id == assetId, ct);
        return await _store.OpenReadAsync(asset.StorageKey, ct);
    }

    private static string Short(string text) => text.Length <= 30 ? text : text[..30] + "...";
}
