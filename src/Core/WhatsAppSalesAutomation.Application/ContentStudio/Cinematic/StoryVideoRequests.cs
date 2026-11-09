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
    Guid Id, Guid ProjectId, string Title, StoryVideoRequestStatus Status, DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, string? VideoUrl, DateTime? VideoReadyAtUtc);

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
    bool CanGenerateVideo);

public sealed record StoryVideoRequestQuery : PagedRequest
{
    public StoryVideoRequestStatus? Status { get; init; }

    public Guid? TenantId { get; init; }

    public string? Search { get; init; }
}

public interface IStoryVideoRequestService
{
    /// <summary>The tenant asks for a video: makes the story from the brief and raises the request. Nothing is charged here; the script is charged when it is written.</summary>
    Task<StoryVideoRequestDto> RaiseAsync(Guid tenantId, Guid? userId, CreateStoryProjectRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<StoryVideoRequestDto>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default);

    Task<PagedResult<PlatformStoryVideoRequestRowDto>> ListAllAsync(StoryVideoRequestQuery query, CancellationToken ct = default);

    Task<PlatformStoryVideoRequestDetailDto> GetAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>The platform admin has the script written for the tenant (charged to the tenant's studio credits, given back when it fails).</summary>
    Task<PlatformStoryVideoRequestDetailDto> GenerateScriptAsync(Guid requestId, Guid? adminUserId, CancellationToken ct = default);

    /// <summary>Stores the finished video under the tenant's folder, records the link, and tells the tenant in-app.</summary>
    Task<PlatformStoryVideoRequestDetailDto> AttachVideoAsync(Guid requestId, Guid? adminUserId, Stream content, string fileName, string contentType, long length, CancellationToken ct = default);
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

    public StoryVideoRequestService(
        IApplicationDbContext context, IStoryStudioService stories, IMediaStorageService storage, ITenantContext tenant, ITenantNotifier notifier, IDateTimeProvider clock)
    {
        _context = context;
        _stories = stories;
        _storage = storage;
        _tenant = tenant;
        _notifier = notifier;
        _clock = clock;
    }

    public async Task<StoryVideoRequestDto> RaiseAsync(Guid tenantId, Guid? userId, CreateStoryProjectRequest request, CancellationToken ct = default)
    {
        var project = await _stories.CreateProjectAsync(tenantId, userId, request, ct);
        var raised = new StoryVideoRequest
        {
            TenantId = tenantId,
            ProjectId = project.Id,
            Status = StoryVideoRequestStatus.Raised,
            RaisedBy = userId,
            RaisedAtUtc = _clock.UtcNow
        };
        _context.StoryVideoRequests.Add(raised);
        await _context.SaveChangesAsync(ct);
        return ToDto(raised, project.Title);
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
        // Waiting requests first (raised, then script written), newest last within a status so the oldest wait is on top; finished ones sink.
        var page = await rows.OrderBy(x => x.r.Status == StoryVideoRequestStatus.VideoReady ? 1 : 0).ThenBy(x => x.r.RaisedAtUtc)
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
        return ToDetail(request, project, tenantName, script);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> GenerateScriptAsync(Guid requestId, Guid? adminUserId, CancellationToken ct = default)
    {
        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        if (request.Status == StoryVideoRequestStatus.VideoReady) throw new ConflictException("The video is already attached; the script cannot be rewritten now.");

        // Charged to the tenant's studio credits (and given back when nothing usable comes), exactly as when a tenant writes a script itself.
        var script = await _stories.GenerateScriptAsync(request.TenantId, adminUserId, project.Id, ct);

        request.Status = StoryVideoRequestStatus.ScriptReady;
        request.ScriptGeneratedAtUtc = _clock.UtcNow;
        await _context.SaveChangesAsync(ct);
        return ToDetail(request, project, tenantName, script);
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
        return ToDetail(request, project, tenantName, script);
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
        new(r.Id, r.ProjectId, title, r.Status, r.RaisedAtUtc, r.ScriptGeneratedAtUtc, LinkFor(r), r.VideoReadyAtUtc);

    private PlatformStoryVideoRequestDetailDto ToDetail(StoryVideoRequest r, StoryProject p, string tenantName, StoryScriptVersionDto? script) =>
        new(r.Id, r.TenantId, tenantName, p.Id, p.Title, p.Language, p.TargetSeconds, r.Status, r.RaisedAtUtc, r.ScriptGeneratedAtUtc, r.VideoReadyAtUtc,
            p.Topic, p.Audience, p.Moral, script, LinkFor(r), r.VideoFileName, r.VideoSource, CanGenerateVideo: false);
}
