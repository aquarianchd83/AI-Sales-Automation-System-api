using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;

public sealed record PlatformPackDto(StoryPlatform Platform, string Title, string Description, IReadOnlyList<string> Hashtags, IReadOnlyList<string> Rules);

public sealed record StoryPublishPackDto(Guid RenderId, IReadOnlyList<PlatformPackDto> Platforms, IReadOnlyList<StoryPublicationDto> Published);

public sealed record StoryPublicationDto(Guid Id, StoryPlatform Platform, string Url, DateTime PublishedAtUtc);

public sealed record RecordPublicationRequest(StoryPlatform Platform, string Url);

public interface IStoryPublishingService
{
    /// <summary>Titles, descriptions and tags ready to paste for each platform, from the approved script. The video itself is the render's download.</summary>
    Task<StoryPublishPackDto> GetPackAsync(Guid tenantId, Guid projectId, CancellationToken ct = default);

    /// <summary>Notes where the user shared the video. The studio does not post for them: that needs each platform's account authorisation and review.</summary>
    Task<StoryPublicationDto> RecordAsync(Guid tenantId, Guid? userId, Guid projectId, RecordPublicationRequest request, CancellationToken ct = default);
}

public class StoryPublishingService : IStoryPublishingService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantConfigOverrideProvider _config;
    private readonly IDateTimeProvider _clock;

    public StoryPublishingService(IApplicationDbContext context, ITenantConfigOverrideProvider config, IDateTimeProvider clock)
    {
        _context = context;
        _config = config;
        _clock = clock;
    }

    public async Task<StoryPublishPackDto> GetPackAsync(Guid tenantId, Guid projectId, CancellationToken ct = default)
    {
        var (project, render, script) = await ApprovedAsync(tenantId, projectId, ct);
        var published = await _context.StoryPublications.IgnoreQueryFilters().Where(p => p.ProjectId == projectId).OrderByDescending(p => p.PublishedAtUtc).ToListAsync(ct);

        var tags = Hashtags(project, script);
        var summary = string.IsNullOrWhiteSpace(script.Description) ? script.StorySummary : script.Description;
        var platforms = new[]
        {
            new PlatformPackDto(StoryPlatform.YouTubeShorts, Clip(script.Title, 90) + " #Shorts", Clip(summary, 4500) + "\n\n" + string.Join(' ', tags.Prepend("#Shorts")),
                tags.Prepend("#Shorts").ToList(),
                new[] { "Title up to 100 characters.", "Vertical video up to 3 minutes counts as a Short.", "A video made with AI may need the \"altered or synthetic content\" label when you upload." }),
            new PlatformPackDto(StoryPlatform.InstagramReels, Clip(script.Title, 120), Clip(summary, 1900) + "\n\n" + string.Join(' ', tags), tags,
                new[] { "Caption up to 2,200 characters.", "Use a professional account for scheduling and insights.", "Label AI-generated content where the app asks." }),
            new PlatformPackDto(StoryPlatform.FacebookReels, Clip(script.Title, 120), Clip(summary, 1900) + "\n\n" + string.Join(' ', tags), tags,
                new[] { "Post it from a Page for insights.", "Label AI-generated content where the app asks." })
        };

        return new StoryPublishPackDto(render.Id, platforms, published.Select(ToDto).ToList());
    }

    public async Task<StoryPublicationDto> RecordAsync(Guid tenantId, Guid? userId, Guid projectId, RecordPublicationRequest request, CancellationToken ct = default)
    {
        var (_, render, _) = await ApprovedAsync(tenantId, projectId, ct);
        var url = (request.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || url.Length > 1000)
            throw new ArgumentException("Paste the address (https://...) of the post.");

        var publication = new StoryPublication
        {
            TenantId = tenantId, ProjectId = projectId, RenderId = render.Id, Platform = request.Platform, Url = url, PublishedAtUtc = _clock.UtcNow, RecordedBy = userId
        };
        _context.StoryPublications.Add(publication);
        await _context.SaveChangesAsync(ct);
        return ToDto(publication);
    }

    private async Task<(StoryProject Project, StoryRender Render, CinematicScript Script)> ApprovedAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        await StudioGate.EnsureEnabledAsync(_config, tenantId, ct);
        var project = await _context.StoryProjects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == projectId && p.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Story project", projectId);
        if (project.Status != StoryProjectStatus.Approved) throw new ConflictException("Approve the video before sharing it.");

        var render = await _context.StoryRenders.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId && r.Status == StoryJobStatus.Completed).OrderByDescending(r => r.CreatedAt).FirstAsync(ct);
        var version = await _context.StoryScriptVersions.IgnoreQueryFilters().FirstAsync(v => v.Id == project.CurrentScriptVersionId, ct);
        var script = CinematicScriptValidator.Validate(version.Json, project.TargetSeconds).Script ?? throw new ConflictException("The approved script could not be read.");
        return (project, render, script);
    }

    private static IReadOnlyList<string> Hashtags(StoryProject project, CinematicScript script)
    {
        var tags = new List<string>();
        foreach (var name in script.Characters.Select(c => c.Name)) tags.Add(Tag(name));
        tags.Add(Tag(project.Language));
        tags.AddRange(new[] { "#Reels", "#ShortStory", "#AIVideo" });
        return tags.Where(t => t.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static string Tag(string text) => "#" + new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());

    private static string Clip(string? text, int max) => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];

    private static StoryPublicationDto ToDto(StoryPublication p) => new(p.Id, p.Platform, p.Url, p.PublishedAtUtc);
}
