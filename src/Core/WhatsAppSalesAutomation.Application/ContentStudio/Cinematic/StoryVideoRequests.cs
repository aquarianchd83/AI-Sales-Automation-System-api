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
    string? RejectedReason,
    // The credits taken when the request was raised, and how many of them went back (all of them, when the video could not be made).
    decimal? PrepaidCredits, decimal? CreditsRefunded);

/// <summary>One request in the platform admin's list, across every tenant.</summary>
public sealed record PlatformStoryVideoRequestRowDto(
    Guid Id, Guid TenantId, string TenantName, Guid ProjectId, string Title, string Language, int TargetSeconds, StoryVideoRequestStatus Status,
    DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, DateTime? VideoReadyAtUtc,
    decimal? PrepaidCredits, decimal? CreditsRefunded);

/// <summary>A request opened by the tenant: where it stands, what was asked for, the script once the platform team has written it, and the video.</summary>
public sealed record StoryVideoRequestDetailDto(
    Guid Id, Guid ProjectId, string Title, string Language, int TargetSeconds, StoryVideoRequestStatus Status,
    DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, DateTime? VideoReadyAtUtc,
    string Topic, string? Audience, string? CallToAction, string? ScriptText,
    string? VideoUrl, string? VideoFileName, string? RejectedReason,
    decimal? PrepaidCredits, decimal? CreditsRefunded);

/// <summary>A request opened by the platform admin: what the tenant asked for, the script so far, and the video if there is one.</summary>
public sealed record PlatformStoryVideoRequestDetailDto(
    Guid Id, Guid TenantId, string TenantName, Guid ProjectId, string Title, string Language, int TargetSeconds, StoryVideoRequestStatus Status,
    DateTime RaisedAtUtc, DateTime? ScriptGeneratedAtUtc, DateTime? VideoReadyAtUtc,
    string Topic, string? Audience, string? CallToAction, string? ScriptText,
    string? VideoUrl, string? VideoFileName, string? VideoSource,
    // False until a real video engine is switched on: the admin can only upload for now.
    bool CanGenerateVideo,
    string? RejectedReason,
    // What the tenant prepaid for this video, in credits, and how much of it was refunded.
    decimal? PrepaidCredits, decimal? CreditsRefunded);

public sealed record RejectStoryVideoRequest(string Reason);

public sealed record SaveStoryVideoScriptRequest(string Text);

/// <summary>The tenant's finished video, opened for reading: the file, the name to save it under, and its type.</summary>
public sealed record StoryVideoDownload(Stream Content, string FileName, string ContentType);

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

    /// <summary>Opens the finished video of one of the tenant's own requests so the tenant can save it. Not found for another tenant's request; a conflict until the video is ready.</summary>
    Task<StoryVideoDownload> OpenVideoAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);

    /// <summary>One of the tenant's own requests in full. Not found for another tenant's.</summary>
    Task<StoryVideoRequestDetailDto> GetForTenantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);

    Task<PagedResult<PlatformStoryVideoRequestRowDto>> ListAllAsync(StoryVideoRequestQuery query, CancellationToken ct = default);

    Task<PlatformStoryVideoRequestDetailDto> GetAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>The platform admin saves the script as plain text (it can be changed until the video is attached). Not charged: the video's price covers it.</summary>
    Task<PlatformStoryVideoRequestDetailDto> SaveScriptTextAsync(Guid requestId, Guid? adminUserId, string text, CancellationToken ct = default);

    /// <summary>Stores the finished video under the tenant's folder, records the link, and tells the tenant in-app.</summary>
    Task<PlatformStoryVideoRequestDetailDto> AttachVideoAsync(Guid requestId, Guid? adminUserId, Stream content, string fileName, string contentType, long length, CancellationToken ct = default);

    /// <summary>Refunds the tenant's prepaid credits because the video cannot be made, and tells the tenant why. Not possible once the video is attached.</summary>
    Task<PlatformStoryVideoRequestDetailDto> RejectAsync(Guid requestId, Guid? adminUserId, string reason, CancellationToken ct = default);
}

public class StoryVideoRequestService : IStoryVideoRequestService
{
    public const long MaxVideoBytes = 300L * 1024 * 1024;
    public const int MaxScriptChars = 20000;

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
            var amounts = await AmountsAsync(new Guid?[] { raised.ChargeActivityId }, ct);
            return ToDto(raised, project.Title, amounts);
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
        var amounts = await AmountsAsync(rows.Select(x => x.Request.ChargeActivityId), ct);
        return rows.Select(x => ToDto(x.Request, x.Title, amounts)).ToList();
    }

    public async Task<StoryVideoRequestDetailDto> GetForTenantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        var row = await (from r in _context.StoryVideoRequests.IgnoreQueryFilters()
                         join p in _context.StoryProjects.IgnoreQueryFilters() on r.ProjectId equals p.Id
                         where r.Id == requestId && r.TenantId == tenantId
                         select new { Request = r, Project = p }).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("That request was not found.");

        var amounts = await AmountsAsync(new Guid?[] { row.Request.ChargeActivityId }, ct);
        var r0 = row.Request;
        var p0 = row.Project;
        return new StoryVideoRequestDetailDto(
            r0.Id, p0.Id, p0.Title, p0.Language, p0.TargetSeconds, r0.Status, r0.RaisedAtUtc, r0.ScriptGeneratedAtUtc, r0.VideoReadyAtUtc,
            p0.Topic, p0.Audience, p0.Moral, r0.ScriptText, LinkFor(r0), r0.VideoFileName, r0.RejectedReason, PaidOf(amounts, r0), RefundedOf(amounts, r0));
    }

    public async Task<StoryVideoDownload> OpenVideoAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        var row = await (from r in _context.StoryVideoRequests.IgnoreQueryFilters()
                         join p in _context.StoryProjects.IgnoreQueryFilters() on r.ProjectId equals p.Id
                         where r.Id == requestId && r.TenantId == tenantId
                         select new { Request = r, p.Title }).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("That request was not found.");

        if (row.Request.Status != StoryVideoRequestStatus.VideoReady || string.IsNullOrEmpty(row.Request.VideoStorageKey))
            throw new ConflictException("The video is not ready yet.");

        Stream stream;
        try { stream = await _storage.OpenReadAsync(row.Request.VideoStorageKey, ct); }
        catch (FileNotFoundException) { throw new NotFoundException("The video file is no longer available. Ask the platform team to upload it again."); }

        var extension = Path.GetExtension(row.Request.VideoFileName ?? row.Request.VideoStorageKey).ToLowerInvariant();
        var type = extension switch { ".webm" => "video/webm", ".mov" => "video/quicktime", _ => "video/mp4" };
        var fileName = !string.IsNullOrWhiteSpace(row.Request.VideoFileName) ? row.Request.VideoFileName! : SafeName(row.Title) + (extension.Length > 0 ? extension : ".mp4");
        return new StoryVideoDownload(stream, fileName, type);
    }

    private static string SafeName(string title)
    {
        var cleaned = new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "video" : cleaned;
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

        var amounts = await AmountsAsync(page.Select(x => x.r.ChargeActivityId), ct);
        var items = page.Select(x => new PlatformStoryVideoRequestRowDto(
            x.r.Id, x.r.TenantId, x.TenantName, x.r.ProjectId, x.p.Title, x.p.Language, x.p.TargetSeconds, x.r.Status,
            x.r.RaisedAtUtc, x.r.ScriptGeneratedAtUtc, x.r.VideoReadyAtUtc,
            PaidOf(amounts, x.r), RefundedOf(amounts, x.r))).ToList();
        return new PagedResult<PlatformStoryVideoRequestRowDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        return await ToDetailAsync(request, project, tenantName, ct);
    }

    public async Task<PlatformStoryVideoRequestDetailDto> SaveScriptTextAsync(Guid requestId, Guid? adminUserId, string text, CancellationToken ct = default)
    {
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length == 0) throw new ArgumentException("Write the script first.");
        if (clean.Length > MaxScriptChars) throw new ArgumentException($"The script is longer than {MaxScriptChars:N0} characters.");

        var (request, project, tenantName) = await LoadAsync(requestId, ct);
        if (request.Status == StoryVideoRequestStatus.VideoReady) throw new ConflictException("The video is already attached; the script cannot be changed now.");
        if (request.Status == StoryVideoRequestStatus.Rejected) throw new ConflictException("This request was refunded.");

        request.ScriptText = clean;
        request.ScriptGeneratedAtUtc = _clock.UtcNow;
        if (request.Status == StoryVideoRequestStatus.Raised) request.Status = StoryVideoRequestStatus.ScriptReady;
        await _context.SaveChangesAsync(ct);
        return await ToDetailAsync(request, project, tenantName, ct);
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

        return await ToDetailAsync(request, project, tenantName, ct);
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
            "Your credits were refunded", $"The video for \"{project.Title}\" could not be made: {clipped} The credits you prepaid for it were refunded.", AlsoWhatsApp: false), ct);

        return await ToDetailAsync(request, project, tenantName, ct);
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

    /// <summary>What each request's charge took and gave back, by studio activity id. A request without a charge (none was needed) is simply absent.</summary>
    private async Task<Dictionary<Guid, (decimal Paid, decimal Refunded)>> AmountsAsync(IEnumerable<Guid?> activityIds, CancellationToken ct)
    {
        var wanted = activityIds.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (wanted.Count == 0) return new();
        var rows = await _context.StudioActivities.IgnoreQueryFilters().Where(a => wanted.Contains(a.Id)).Select(a => new { a.Id, a.Units, a.UnitsRefunded }).ToListAsync(ct);
        return rows.ToDictionary(a => a.Id, a => (a.Units, a.UnitsRefunded));
    }

    private static decimal? PaidOf(Dictionary<Guid, (decimal Paid, decimal Refunded)> amounts, StoryVideoRequest r) =>
        r.ChargeActivityId is { } id && amounts.TryGetValue(id, out var a) ? a.Paid : null;

    private static decimal? RefundedOf(Dictionary<Guid, (decimal Paid, decimal Refunded)> amounts, StoryVideoRequest r) =>
        r.ChargeActivityId is { } id && amounts.TryGetValue(id, out var a) ? a.Refunded : null;

    private StoryVideoRequestDto ToDto(StoryVideoRequest r, string title, Dictionary<Guid, (decimal Paid, decimal Refunded)> amounts) =>
        new(r.Id, r.ProjectId, title, r.Status, r.RaisedAtUtc, r.ScriptGeneratedAtUtc, LinkFor(r), r.VideoReadyAtUtc, r.RejectedReason, PaidOf(amounts, r), RefundedOf(amounts, r));

    private async Task<PlatformStoryVideoRequestDetailDto> ToDetailAsync(StoryVideoRequest r, StoryProject p, string tenantName, CancellationToken ct)
    {
        var amounts = await AmountsAsync(new Guid?[] { r.ChargeActivityId }, ct);

        return new(r.Id, r.TenantId, tenantName, p.Id, p.Title, p.Language, p.TargetSeconds, r.Status, r.RaisedAtUtc, r.ScriptGeneratedAtUtc, r.VideoReadyAtUtc,
            p.Topic, p.Audience, p.Moral, r.ScriptText, LinkFor(r), r.VideoFileName, r.VideoSource, CanGenerateVideo: false, r.RejectedReason, PaidOf(amounts, r), RefundedOf(amounts, r));
    }
}
