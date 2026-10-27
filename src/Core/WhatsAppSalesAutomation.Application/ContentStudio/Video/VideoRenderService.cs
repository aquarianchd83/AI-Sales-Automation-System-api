using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Video;

/// <summary>
/// Starts, lists, cancels and plays back video renders. A video is made from a script and its pictures, so every scene must have a picture
/// before it starts; a script written by the platform's test mode is never turned into a video; a tenant makes one video at a time and the
/// platform only so many at once. The work is charged first (a render, and the voice-over) and returned if it fails, is cancelled or never
/// starts. When the platform's video setting is "Simulator" no money is spent anywhere: the job runs through its steps and ends as a test
/// render with no video, and says so.
/// </summary>
public sealed class VideoRenderService : IVideoRenderService
{
    private static readonly RenderJobStatus[] Active = { RenderJobStatus.Queued, RenderJobStatus.Preparing, RenderJobStatus.Rendering };

    private readonly IApplicationDbContext _db;
    private readonly ITenantDocumentStore _store;
    private readonly IVideoRenderQueue _queue;
    private readonly IStudioMeteringService _metering;
    private readonly IAiFeatureRouting _routing;
    private readonly IOptionsSnapshot<RenderOptions> _options;
    private readonly IVideoRenderer _renderer;
    private readonly IVoiceSynthesizer _voice;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;

    public VideoRenderService(
        IApplicationDbContext db, ITenantDocumentStore store, IVideoRenderQueue queue, IStudioMeteringService metering, IAiFeatureRouting routing, IOptionsSnapshot<RenderOptions> options,
        IVideoRenderer renderer, IVoiceSynthesizer voice, ICurrentUserService user, IDateTimeProvider clock)
    {
        _db = db;
        _store = store;
        _queue = queue;
        _metering = metering;
        _routing = routing;
        _options = options;
        _renderer = renderer;
        _voice = voice;
        _user = user;
        _clock = clock;
    }

    public async Task<RenderJobDto> StartAsync(Guid tenantId, Guid versionId, StartRenderRequest request, CancellationToken ct = default)
    {
        var version = await _db.ContentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentVersion", versionId);
        var project = await _db.ContentProjects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == version.ProjectId && p.TenantId == tenantId && !p.IsDeleted, ct)
            ?? throw new NotFoundException("ContentVersion", versionId);

        var aspect = (request?.AspectRatio ?? "16:9").Trim();
        if (!RenderLimits.AspectRatios.Contains(aspect))
            throw Invalid("aspectRatio", "The shape must be 16:9 (wide), 9:16 (tall) or 1:1 (square).");
        var gender = string.Equals(request?.VoiceGender?.Trim(), "Male", StringComparison.OrdinalIgnoreCase) ? "Male" : "Female";

        if (version.IsSimulated)
            throw new ConflictException("This script was written by the platform's test mode, so it says nothing real about your business and cannot be made into a video. Write a new script once the AI is connected.");

        var scenes = await _db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && s.VersionId == versionId).ToListAsync(ct);
        if (scenes.Count == 0)
            throw new ConflictException("This script has no scenes.");
        var without = scenes.Count(s => s.ChosenAssetId == null);
        if (without > 0)
            throw new ConflictException($"{without} of {scenes.Count} scenes have no picture yet. Choose a picture for every scene first.");

        var simulated = !_routing.Resolve(AiFeatures.VideoRenders).UsesThirdParty;
        if (!simulated)
        {
            var options = _options.Value;
            if (!options.RendererConfigured || !options.VoiceConfigured)
                throw new ConflictException("Making videos is not set up on this platform yet: it needs a video service key and a voice key. Ask the platform administrator.");
            if (!_store.IsConfigured)
                throw new ConflictException("File storage (AWS S3) is not set up yet, so a video cannot be kept. Ask the platform administrator to check the AWS Settings.");
        }

        // One at a time for a tenant, and only so many at once for the whole platform.
        if (await _db.RenderJobs.AnyAsync(j => j.TenantId == tenantId && Active.Contains(j.Status), ct))
            throw new ConflictException("A video is already being made for your business. Wait for it to finish, or cancel it, then start another.");
        var running = await _db.RenderJobs.IgnoreQueryFilters().CountAsync(j => Active.Contains(j.Status), ct);
        if (running >= _options.Value.ConcurrencyLimit)
            throw new ConflictException("Many videos are being made right now. Try again in a few minutes.");

        var job = new RenderJob
        {
            TenantId = tenantId, ProjectId = project.Id, VersionId = versionId, AspectRatio = aspect, VoiceGender = gender, LanguageCode = VoiceLanguages.CodeFor(project.Language),
            Renderer = simulated ? "Simulator" : _renderer.Name, IsSimulated = simulated, IsAiVoice = !simulated, ContentHash = version.ContentHash,
            FlaggedScenes = scenes.Count(s => s.ClaimStatus is ClaimStatus.Unverified or ClaimStatus.Unsupported), Stage = "Waiting for its turn", CreatedBy = _user.UserId
        };

        if (!simulated)
        {
            job.RenderActivityId = await ChargeAsync(tenantId, StudioActivityKind.VideoRender, job, $"Video for \"{project.Title}\" (version {version.Number})", ct);
            try
            {
                job.VoiceActivityId = await ChargeAsync(tenantId, StudioActivityKind.VoiceOver, job, $"Voice-over for \"{project.Title}\"", ct);
            }
            catch
            {
                await _metering.RefundAsync(job.RenderActivityId!.Value, "The voice-over could not be charged.", CancellationToken.None);
                throw;
            }
        }

        _db.RenderJobs.Add(job);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            await RefundAsync(job, "The video could not be started.");
            throw;
        }

        _queue.EnqueueStart(job.Id, tenantId);
        return ToDto(job, version.Number);
    }

    public async Task<IReadOnlyList<RenderJobDto>> ListAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await _db.ContentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentVersion", versionId);
        var jobs = await _db.RenderJobs.AsNoTracking().Where(j => j.TenantId == tenantId && j.VersionId == versionId).OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(ct);
        return jobs.Select(j => ToDto(j, version.Number)).ToList();
    }

    public async Task<RenderJobDto> GetAsync(Guid tenantId, Guid jobId, CancellationToken ct = default)
    {
        var job = await FindAsync(tenantId, jobId, ct, track: false);
        return ToDto(job, await NumberAsync(tenantId, job.VersionId, ct));
    }

    public async Task<RenderJobDto> CancelAsync(Guid tenantId, Guid jobId, CancellationToken ct = default)
    {
        var job = await FindAsync(tenantId, jobId, ct);
        if (!Active.Contains(job.Status))
            throw new ConflictException("This video has already finished, so there is nothing to cancel.");

        // The background step notices the flag at its next check. A job still waiting is cancelled here and now.
        job.CancelRequested = true;
        if (job.Status == RenderJobStatus.Queued)
        {
            job.Status = RenderJobStatus.Cancelled;
            job.Stage = "Cancelled by you";
            job.FinishedAt = _clock.UtcNow;
            await RefundAsync(job, "Cancelled before it started.");
        }
        else
        {
            job.Stage = "Cancelling…";
        }

        job.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(job, await NumberAsync(tenantId, job.VersionId, ct));
    }

    public async Task<VideoFile> OpenVideoAsync(Guid tenantId, Guid jobId, CancellationToken ct = default)
    {
        var job = await FindAsync(tenantId, jobId, ct, track: false);
        if (string.IsNullOrEmpty(job.OutputStorageKey))
            throw new NotFoundException(job.IsSimulated ? "This was a test render, so there is no video." : "This video is not ready yet.");
        // Defence in depth: the key must sit in this tenant's own folder, whatever the row says.
        if (!job.OutputStorageKey.Contains(_store.FolderOf(tenantId), StringComparison.Ordinal))
            throw new NotFoundException("RenderJob", jobId);
        return new VideoFile(await _store.OpenReadAsync(job.OutputStorageKey, ct), "video/mp4", job.OutputBytes);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private async Task<Guid> ChargeAsync(Guid tenantId, StudioActivityKind kind, RenderJob job, string description, CancellationToken ct)
    {
        var charge = await _metering.ChargeAsync(new StudioChargeRequest(tenantId, kind, $"render:{kind}:{job.Id:N}", description, null, nameof(RenderJob), job.Id.ToString(), job.Renderer, _user.UserId), ct);
        if (!charge.Allowed)
            throw new ConflictException(charge.Message ?? "Your studio quota does not cover this right now.");
        return charge.ActivityId;
    }

    /// <summary>Returns whatever this job was charged. Safe to call more than once.</summary>
    private async Task RefundAsync(RenderJob job, string reason)
    {
        foreach (var id in new[] { job.RenderActivityId, job.VoiceActivityId })
            if (id is { } activity)
                await _metering.RefundAsync(activity, reason, CancellationToken.None);
    }

    private async Task<RenderJob> FindAsync(Guid tenantId, Guid jobId, CancellationToken ct, bool track = true)
    {
        var query = track ? _db.RenderJobs.AsQueryable() : _db.RenderJobs.AsNoTracking();
        return await query.FirstOrDefaultAsync(j => j.Id == jobId && j.TenantId == tenantId, ct) ?? throw new NotFoundException("RenderJob", jobId);
    }

    private async Task<int> NumberAsync(Guid tenantId, Guid versionId, CancellationToken ct) =>
        await _db.ContentVersions.AsNoTracking().Where(v => v.Id == versionId && v.TenantId == tenantId).Select(v => v.Number).FirstOrDefaultAsync(ct);

    public static RenderJobDto ToDto(RenderJob j, int versionNumber) => new(
        j.Id, j.VersionId, versionNumber, j.Status.ToString(), j.Progress, j.Stage, j.AspectRatio, j.VoiceGender, j.Renderer, j.IsSimulated, j.IsAiVoice,
        !string.IsNullOrEmpty(j.OutputStorageKey), j.OutputBytes, j.OutputSeconds, j.Error, j.FlaggedScenes, j.CreatedAt, j.StartedAt, j.FinishedAt);

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
