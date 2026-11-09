using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

/// <summary>One of the tenant's own requests, as the Story studio lists it next to the story.</summary>
public sealed record StoryVideoRequestDto(
    Guid Id, Guid ProjectId, string Title, StoryVideoRequestStatus Status, DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, string? VideoUrl, DateTime? VideoReadyAtUtc,
    string? RejectedReason);

/// <summary>One request in the platform admin's list, across every tenant.</summary>
public sealed record PlatformStoryVideoRequestRowDto(
    Guid Id, Guid TenantId, string TenantName, Guid ProjectId, string Title, string Language, int TargetSeconds, StoryVideoRequestStatus Status,
    DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, DateTime? VideoReadyAtUtc);

/// <summary>A request opened by the platform admin: what the tenant asked for, the script so far, and the video if there is one.</summary>
public sealed record PlatformStoryVideoRequestDetailDto(
    Guid Id, Guid TenantId, string TenantName, Guid ProjectId, string Title, string Language, int TargetSeconds, StoryVideoRequestStatus Status,
    DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, DateTime? VideoReadyAtUtc,
    string Topic, string? Audience, string? CallToAction, StoryScriptVersionDto? Script,
    string? VideoUrl, string? VideoFileName, string? VideoSource,
    // False until a real video engine is switched on: the admin can only upload for now.
    bool CanGenerateVideo,
    string? RejectedReason,
    // What the tenant paid for this video, in credits (after any refund).
    decimal? CreditsCharged);

public sealed record RejectStoryVideoRequest(string Reason);

public sealed record StoryVideoRequestQuery : PagedRequest
{
    public StoryVideoRequestStatus? Status { get; init; }

    public Guid? TenantId { get; init; }

    public string? Search { get; init; }
}

public interface IStoryVideoRequestService
{
    /// <summary>The tenant asks for a video: the video's price (the platform's video render credit cost) is taken first - nothing is made for a tenant who cannot pay - then the story and the request are made.
    /// Throws a payment-required error, having created nothing, when the credits are short.</summary>
    Task<StoryVideoRequestDto> RaiseAsync(Guid tenantId, Guid? userId, CreateStoryProjectRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<StoryVideoRequestDto>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default);

    Task<PagedResult<PlatformStoryVideoRequestRowDto>> ListAllAsync(StoryVideoRequestQuery query, CancellationToken ct = default);

    Task<PlatformStoryVideoRequestDetailDto> GetAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>The platform admin has the script written for the tenant. Not charged again: the video's price covers it.</summary>
    Task<PlatformStoryVideoRequestDetailDto> GenerateScriptAsync(Guid requestId, Guid? adminUserId, CancellationToken ct = default);

    /// <summary>Stores the finished video under the tenant's folder, records the link, and tells the tenant in-app.</summary>
    Task<PlatformStoryVideoRequestDetailDto> AttachVideoAsync(Guid requestId, Guid? adminUserId, Stream content, string fileName, string contentType, long length, CancellationToken ct = default);

    /// <summary>The video cannot be made: the credits go back to the tenant and the tenant is told why. Not possible once the video is attached.</summary>
    Task<PlatformStoryVideoRequestDetailDto> RejectAsync(Guid requestId, Guid? adminUserId, string reason, CancellationToken ct = default);
}

public class StoryVideoRequestService : IStoryVideoRequestService
{
    public const long MaxVideoBytes = 300L * 1024 * 1024;

    private static readonly string[] AllowedExtensions = { ".mp4", ".webm", ".mov", ".m4v" };

    private readonly IApplicationDbContext _context;
    private readonly IStoryStudioService _stories;
    private readonly IMediaStorageService _storage;
    private readonly ITenantContext _tenant;
    private readonly ITenantNotifier _notifier;
    private readonly IDateTimeProvider _clock;
    private readonly IStudioMeteringService _metering;

    public StoryVideoRequestService(
        IApplicationDbContext context, IStoryStudioService stories, IMediaStorageService storage, ITenantContext tenant, ITenantNotifier notifier, IDateTimeProvider clock,
        IStudioMeteringService metering)
    {
        _metering = metering;
        _context = context;
        _stories = stories;
        _storage = storage;
        _tenant = tenant;
        _notifier = notifier;
        _clock = clock;
    }

    public async Task<StoryVideoRequestDto> RaiseAsync(Guid tenantId, Guid? userId, CreateStoryProjectRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();

        // The price comes first: a tenant who cannot pay gets nothing made, and nothing left behind.
        var charge = await _metering.ChargeAsync(new StudioChargeRequest(
            tenantId, StudioActivityKind.VideoRender, $"story-video-request:{id:N}", "Video requested in the Story studio",
            ReferenceType: nameof(StoryVideoRequest), ReferenceId: id.ToString(), ActorUserId: userId), ct);
        if (!charge.Allowed) throw new PlanLimitExceededException(charge.Message ?? "You don't have enough credits for a video. Buy credits to continue.");

        try
        {
            var project = await _stories.CreateProjectAsync(tenantId, userId, request, ct);
            var raised = new StoryVideoRequest
            {
                Id = id,
                TenantId = tenantId,
                ProjectId = project.Id,
                Status = StoryVideoRequestStatus.Raised,
                RaisedBy = userId,
                RaisedAtUtc = _clock.UtcNow,
                ChargeActivityId = charge.ActivityId
            };
            _context.StoryVideoRequests.Add(raised);
            await _context.SaveChangesAsync(ct);
            return ToDto(raised, project.Title);
        }
        catch
        {
            // Nothing was made, so nothing is kept.
            await _metering.RefundAsync(charge.ActivityId, "The request could not be saved.", CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<StoryVideoRequestDto>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rows = await (from r in _context.StoryVideoRequests.IgnoreQueryFilters()
                          join p in _context.StoryProjects.IgnoreQueryFilters() on r.ProjectId equals p.Id
                          where r.TenantId == tenantId
                          orderby r.RaisedAtUtc descending
                          select new { Request = r, p.Title }).ToListAsync(ct);
        return rows.Select(x => ToDto(x.Request, x.Title)).ToList();
    }

    public async Task<PagedResult<PlatformStoryVideoRequestRowDto>> ListAllAsync(StoryVideoRequestQuery query, CancellationToken ct = default)
    {
        var rows = from r in _context.StoryVideoRequests.IgnoreQueryFilters()
                   join p in _context.StoryProjects.IgnoreQueryFilters() on r.ProjectId equals p.Id
                   join t in _context.Tenants.IgnoreQueryFilters() on r.TenantId equals t.Id
                   select new { r, p, TenantName = t.Name };

        if (query.Status is { } status) rows = rows.Where(x => x.r.Status == status);
        if (query.TenantId is { } tenantId) rows = rows.Where(x => x.r.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(x => x.p.Title.Contains(term) || x.TenantName.Contains(term));
        }

        var total = await rows.CountAsync(ct);
        // Waiting requests first, the oldest wait on top; finished and declined ones sink.
        var page = await rows.OrderBy(x => x.r.Status == StoryVideoRequestStatus.VideoReady || x.r.Status == StoryVideoRequestStatus.Rejected ? 1 : 0).ThenBy(x => x.r.RaisedAtUtc)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);

        var items = page.Select(x => new PlatformStoryVideoRequestRowDto(
            x.r.Id, x.r.TenantId, x.TenantName, x.r.ProjectId, x.p.Title, x.p.Language, x.p.TargetSeconds, x.r.Status,
            x.r.RaisedAtUtc, x.r.ScriptGeneratedAtUtc, x.r.VideoReadyAtUtc)).ToList();
        return new PagedResult<PlatformStoryVideoRequestRowDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        var script = await _stories.GetCurrentScriptAsync(request.TenantId, project.Id, ct);
        return await ToDetailAsync(request, project, tenantName, script, ct);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> GenerateScriptAsync(Guid requestId, Guid? adminUserId, CancellationToken ct = default)
    {
        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        if (request.Status == StoryVideoRequestStatus.VideoReady) throw new ConflictException("The video is already attached; the script cannot be rewritten now.");
        if (request.Status == StoryVideoRequestStatus.Rejected) throw new ConflictException("This request was declined.");

        // Not charged again: what the tenant paid when raising the request covers the script (and what the platform pays the AI for it).
        var script = await _stories.GenerateScriptAsync(request.TenantId, adminUserId, project.Id, ct, chargeTenant: false);

        request.Status = StoryVideoRequestStatus.ScriptReady;
        request.ScriptGeneratedAtUtc = _clock.UtcNow;
        await _context.SaveChangesAsync(ct);
        return await ToDetailAsync(request, project, tenantName, script, ct);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> AttachVideoAsync(
        Guid requestId, Guid? adminUserId, Stream content, string fileName, string contentType, long length, CancellationToken ct = default)
    {
        if (length <= 0) throw new ArgumentException("Choose a video file to upload.");
        if (length > MaxVideoBytes) throw new ArgumentException($"The video is larger than {MaxVideoBytes / (1024 * 1024)} MB.");
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension) || contentType is null || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Upload a video file (MP4, WebM or MOV).");

        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        if (request.Status == StoryVideoRequestStatus.Rejected) throw new ConflictException("This request was declined, so no video can be attached to it.");
        var previousKey = request.VideoStorageKey;

        // The storage puts a file under the folder of the tenant it is acting as; the platform admin acts as nobody, so act as the request's tenant.
        _tenant.SetTenant(request.TenantId);
        var stored = await _storage.UploadAsync(content, fileName!, contentType, ct);

        var now = _clock.UtcNow;
        request.VideoStorageKey = stored.StorageKey;
        request.VideoUrl = stored.Url;
        request.VideoFileName = Path.GetFileName(fileName);
        request.VideoSource = "Uploaded";
        request.VideoReadyAtUtc = now;
        request.VideoAttachedBy = adminUserId;
        request.Status = StoryVideoRequestStatus.VideoReady;
        await _context.SaveChangesAsync(ct);

        // The video is delivered: the credits taken when it was requested are now earned.
        if (request.ChargeActivityId is { } paid) await _metering.CompleteAsync(paid, ct);

        if (!string.IsNullOrEmpty(previousKey))
        {
            try { await _storage.DeleteAsync(previousKey, ct); }
            catch (Exception) { /* the replaced file is only orphaned: the new one is already recorded */ }
        }

        // One notice per attachment, so replacing the video tells the tenant again.
        await _notifier.NotifyAsync(new TenantNotificationRequest(
            request.TenantId, TenantNotificationKind.StoryVideoReady, null, $"story-video:{request.Id:N}:{now.Ticks}",
            "Your video is ready", $"The video for \"{project.Title}\" is ready. Open Story studio to watch it.", AlsoWhatsApp: false), ct);

        var script = await _stories.GetCurrentScriptAsync(request.TenantId, project.Id, ct);
        return await ToDetailAsync(request, project, tenantName, script, ct);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> RejectAsync(Guid requestId, Guid? adminUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Say why the request is declined: the tenant is told.");
        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        if (request.Status == StoryVideoRequestStatus.VideoReady) throw new ConflictException("The video is already attached, so the request cannot be declined.");
        if (request.Status == StoryVideoRequestStatus.Rejected) throw new ConflictException("This request was already declined.");

        var clipped = reason.Trim();
        if (clipped.Length > 500) clipped = clipped[..500];

        if (request.ChargeActivityId is { } paid) await _metering.RefundAsync(paid, "Video request declined: " + clipped, ct);

        var now = _clock.UtcNow;
        request.Status = StoryVideoRequestStatus.Rejected;
        request.RejectedReason = clipped;
        request.RejectedAtUtc = now;
        await _context.SaveChangesAsync(ct);

        await _notifier.NotifyAsync(new TenantNotificationRequest(
            request.TenantId, TenantNotificationKind.StoryVideoRejected, null, $"story-video-rejected:{request.Id:N}",
            "Your video request was declined", $"The video for \"{project.Title}\" could not be made: {clipped} The credits for it were returned to you.", AlsoWhatsApp: false), ct);

        var script = await _stories.GetCurrentScriptAsync(request.TenantId, project.Id, ct);
        return await ToDetailAsync(request, project, tenantName, script, ct);
    }

    private async Task<(StoryVideoRequest Request, StoryProject Project, string TenantName)> LoadAsync(Guid requestId, CancellationToken ct)
    {
        var request = await _context.StoryVideoRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == requestId, ct)
                      ?? throw new NotFoundException("That request was not found.");
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == request.ProjectId, ct);
        var tenantName = await _context.Tenants.IgnoreQueryFilters().Where(t => t.Id == request.TenantId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        return (request, project, tenantName);
    }

    private string? LinkFor(StoryVideoRequest r) =>
        !string.IsNullOrEmpty(r.VideoStorageKey) ? _storage.GetPublicUrl(r.VideoStorageKey) : r.VideoUrl;

    private StoryVideoRequestDto ToDto(StoryVideoRequest r, string title) =>
        new(r.Id, r.ProjectId, title, r.Status, r.RaisedAtUtc, r.ScriptGeneratedAtUtc, LinkFor(r), r.VideoReadyAtUtc, r.RejectedReason);

    private async Task<PlatformStoryVideoRequestDetailDto> ToDetailAsync(StoryVideoRequest r, StoryProject p, string tenantName, StoryScriptVersionDto? script, CancellationToken ct)
    {
        decimal? charged = null;
        if (r.ChargeActivityId is { } activityId)
            charged = await _context.StudioActivities.IgnoreQueryFilters().Where(a => a.Id == activityId).Select(a => (decimal?)(a.Units - a.UnitsRefunded)).FirstOrDefaultAsync(ct);

        return new(r.Id, r.TenantId, tenantName, p.Id, p.Title, p.Language, p.TargetSeconds, r.Status, r.RaisedAtUtc, r.ScriptGeneratedAtUtc, r.VideoReadyAtUtc,
            p.Topic, p.Audience, p.Moral, script, LinkFor(r), r.VideoFileName, r.VideoSource, CanGenerateVideo: false, r.RejectedReason, charged);
    }
}
