using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

/// <summary>
/// Connecting a tenant's YouTube channel and publishing approved videos to it. Connecting follows the same pattern as the ads login: a state
/// token this server issued, for this tenant, that expires; tokens are encrypted at rest and never sent anywhere but Google. Publishing is
/// refused unless the video is that of an approved, unchanged script (<see cref="PublishGate"/>); a request carries an idempotency key so the same
/// click twice is one publication; the platform-wide daily limit protects Google's quota; and the video always tells YouTube it contains a
/// synthetic voice. Every publish, cancel and disconnect is written to the tenant's audit log.
/// </summary>
public sealed class PublishingService : IPublishingService
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly IYouTubeClient _youtube;
    private readonly IPublicationQueue _queue;
    private readonly IOptionsSnapshot<SocialOptions> _options;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditTrailWriter _audit;
    private readonly ILogger<PublishingService> _logger;

    public PublishingService(
        IApplicationDbContext db, ISecretProtector protector, IYouTubeClient youtube, IPublicationQueue queue, IOptionsSnapshot<SocialOptions> options, ICurrentUserService user,
        IDateTimeProvider clock, IAuditTrailWriter audit, ILogger<PublishingService> logger)
    {
        _db = db;
        _protector = protector;
        _youtube = youtube;
        _queue = queue;
        _options = options;
        _user = user;
        _clock = clock;
        _audit = audit;
        _logger = logger;
    }

    public bool YouTubeAvailable => _youtube.IsConfigured;

    // ----- connections -----------------------------------------------------------------------------------------------------------

    public Task<ConnectUrlDto> GetConnectUrlAsync(Guid tenantId, string redirectUri, CancellationToken ct = default)
    {
        EnsureConfigured();
        EnsureValidRedirect(redirectUri);
        var state = _protector.Protect($"{tenantId:N}|{(_clock.UtcNow + PublishingLimits.StateLifetime).Ticks}");
        return Task.FromResult(new ConnectUrlDto(_youtube.BuildLoginUrl(redirectUri, state)));
    }

    public async Task<ConnectionDto> ConnectAsync(Guid tenantId, CompleteConnectRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        EnsureValidRedirect(request?.RedirectUri ?? string.Empty);
        EnsureStateIsOurs(tenantId, request!.State);
        if (string.IsNullOrWhiteSpace(request.Code))
            throw Invalid("code", "Google did not send back a sign-in code. Please click Connect again.");

        OAuthTokens tokens;
        YouTubeChannel? channel;
        try
        {
            tokens = await _youtube.ExchangeCodeAsync(request.Code, request.RedirectUri, ct);
            if (string.IsNullOrEmpty(tokens.RefreshToken))
                throw new ConflictException("Google did not give long-term access. Please click Connect again and allow every permission it asks for.");
            if (!tokens.Scopes.Contains("youtube.upload", StringComparison.Ordinal))
                throw new ConflictException("Uploading videos was not allowed. Please click Connect again and allow it - without it nothing can be published.");
            channel = await _youtube.GetChannelAsync(tokens.AccessToken, ct);
        }
        catch (SocialProviderException ex)
        {
            throw new ConflictException(ex.Message);
        }

        if (channel is null)
            throw new ConflictException("That Google account has no YouTube channel. Create a channel on YouTube first, then connect again.");

        var existing = await _db.SocialConnections.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Platform == SocialPlatform.YouTube && c.ExternalAccountId == channel.Id, ct);
        var connection = existing ?? new SocialConnection { TenantId = tenantId, Platform = SocialPlatform.YouTube, ExternalAccountId = channel.Id };
        connection.AccountName = channel.Title;
        connection.Status = SocialConnectionStatus.Connected;
        connection.EncryptedAccessToken = _protector.Protect(tokens.AccessToken);
        connection.EncryptedRefreshToken = _protector.Protect(tokens.RefreshToken!);
        connection.TokenExpiresAtUtc = tokens.ExpiresAtUtc;
        connection.Scopes = tokens.Scopes;
        connection.ConnectedBy = _user.UserId;
        connection.LastError = null;
        connection.UpdatedAt = _clock.UtcNow;
        if (existing is null)
            _db.SocialConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(tenantId, nameof(SocialConnection), connection.Id, existing is null ? AuditAction.Create : AuditAction.Update,
            new Dictionary<string, object?> { ["platform"] = "YouTube", ["account"] = channel.Title }, ct);
        return ToDto(connection);
    }

    public async Task<IReadOnlyList<ConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken ct = default) =>
        (await _db.SocialConnections.AsNoTracking().Where(c => c.TenantId == tenantId).OrderBy(c => c.AccountName).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task DisconnectAsync(Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        var connection = await _db.SocialConnections.FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == tenantId, ct) ?? throw new NotFoundException("SocialConnection", connectionId);

        // Nothing waiting to go out through it is sent after the tenant disconnected the account.
        var waiting = await _db.Publications.Where(p => p.TenantId == tenantId && p.ConnectionId == connectionId && p.Status == PublicationStatus.Scheduled).ToListAsync(ct);
        foreach (var publication in waiting)
        {
            publication.Status = PublicationStatus.Cancelled;
            publication.LastError = "The account was disconnected before this was sent.";
            publication.NextRetryAtUtc = null;
            publication.UpdatedAt = _clock.UtcNow;
        }

        var refresh = _protector.TryUnprotect(connection.EncryptedRefreshToken);
        _db.SocialConnections.Remove(connection);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(tenantId, nameof(SocialConnection), connectionId, AuditAction.Delete,
            new Dictionary<string, object?> { ["platform"] = connection.Platform.ToString(), ["account"] = connection.AccountName, ["cancelledPublications"] = waiting.Count }, ct);

        if (refresh is not null)
        {
            try
            {
                await _youtube.RevokeAsync(refresh, ct);
            }
            catch (Exception ex) when (ex is SocialProviderException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Google did not confirm revoking access for a disconnected channel; the saved tokens are deleted regardless.");
            }
        }
    }

    // ----- publishing ------------------------------------------------------------------------------------------------------------

    public async Task<PublicationDto> PublishAsync(Guid tenantId, Guid versionId, PublishVideoRequest request, CancellationToken ct = default)
    {
        var version = await _db.ContentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentVersion", versionId);
        var project = await _db.ContentProjects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == version.ProjectId && p.TenantId == tenantId && !p.IsDeleted, ct)
            ?? throw new NotFoundException("ContentVersion", versionId);

        var key = request.IdempotencyKey?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length > 100)
                throw Invalid("idempotencyKey", "The request key is too long.");
            var same = await _db.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == key, ct);
            if (same is not null)
                return ToDto(same, version.Number); // the same click twice: the publication already made, not a second one
        }

        var title = Clean(request.Title, PublishingLimits.MaxTitle) ?? throw Invalid("title", "Give the video a title.");
        var privacy = (request.Privacy ?? "private").Trim().ToLowerInvariant();
        if (!PublishingLimits.Privacies.Contains(privacy))
            throw Invalid("privacy", "Choose private, unlisted or public.");

        var now = _clock.UtcNow;
        DateTime? scheduled = null;
        if (request.ScheduledAtUtc is { } when)
        {
            var at = DateTime.SpecifyKind(when, DateTimeKind.Utc);
            if (at > now.AddMinutes(1))
            {
                if (at - now > PublishingLimits.MaxScheduleAhead)
                    throw Invalid("scheduledAtUtc", "A video can be scheduled up to 90 days ahead.");
                scheduled = at;
            }
        }

        var connection = await _db.SocialConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ConnectionId && c.TenantId == tenantId, ct)
            ?? throw new NotFoundException("SocialConnection", request.ConnectionId);
        if (connection.Status != SocialConnectionStatus.Connected)
            throw new ConflictException($"Your YouTube account \"{connection.AccountName}\" needs to be connected again before anything can be published to it.");

        var render = await _db.RenderJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == request.RenderJobId && j.TenantId == tenantId, ct);
        var problem = await PublishGate.ProblemAsync(_db, tenantId, version, render, ct);
        if (problem is not null)
            throw new ConflictException(problem);

        if (await _db.Publications.AnyAsync(p => p.TenantId == tenantId && p.RenderJobId == render!.Id && p.ConnectionId == connection.Id && p.Status != PublicationStatus.Failed && p.Status != PublicationStatus.Cancelled, ct))
            throw new ConflictException("This video is already published, or waiting to be published, to that account.");

        var since = now.AddHours(-24);
        var sentToday = await _db.Publications.IgnoreQueryFilters().CountAsync(p => p.CreatedAt >= since && p.Status != PublicationStatus.Failed && p.Status != PublicationStatus.Cancelled, ct);
        if (sentToday >= _options.Value.DailyLimit)
            throw new ConflictException("YouTube only lets this platform send a few videos a day, and today's allowance is used up. Try again tomorrow.");

        var publication = new Publication
        {
            TenantId = tenantId, ProjectId = project.Id, VersionId = versionId, RenderJobId = render!.Id, ConnectionId = connection.Id, Platform = connection.Platform, AccountName = connection.AccountName,
            Title = title, Description = await BuildDescriptionAsync(tenantId, versionId, request.Description, ct), Privacy = privacy, ScheduledAtUtc = scheduled,
            IdempotencyKey = string.IsNullOrEmpty(key) ? Guid.NewGuid().ToString("N") : key, ApprovedHash = version.ApprovedHash!, CreatedBy = _user.UserId
        };
        _db.Publications.Add(publication);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A second click with the same key won the race: hand back the one that exists.
            var winner = await _db.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == publication.IdempotencyKey, ct);
            if (winner is not null)
                return ToDto(winner, version.Number);
            throw;
        }

        await _audit.RecordAsync(tenantId, nameof(Publication), publication.Id, AuditAction.Create,
            new Dictionary<string, object?> { ["platform"] = "YouTube", ["account"] = connection.AccountName, ["version"] = version.Number, ["privacy"] = privacy, ["scheduled"] = scheduled?.ToString("O") }, ct);
        _queue.Enqueue(publication.Id, tenantId, scheduled is null ? null : scheduled.Value - now);
        return ToDto(publication, version.Number);
    }

    public async Task<IReadOnlyList<PublicationDto>> ListAsync(Guid tenantId, Guid versionId, CancellationToken ct = default)
    {
        var version = await _db.ContentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct) ?? throw new NotFoundException("ContentVersion", versionId);
        var rows = await _db.Publications.AsNoTracking().Where(p => p.TenantId == tenantId && p.VersionId == versionId).OrderByDescending(p => p.CreatedAt).Take(30).ToListAsync(ct);
        return rows.Select(p => ToDto(p, version.Number)).ToList();
    }

    public async Task<PublicationDto> CancelAsync(Guid tenantId, Guid publicationId, CancellationToken ct = default)
    {
        var publication = await FindAsync(tenantId, publicationId, ct);
        if (publication.Status != PublicationStatus.Scheduled)
            throw new ConflictException(publication.Status == PublicationStatus.Uploading
                ? "The video is being sent right now and cannot be stopped."
                : "This has already finished, so there is nothing to cancel.");

        publication.Status = PublicationStatus.Cancelled;
        publication.NextRetryAtUtc = null;
        publication.LastError = null;
        publication.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(tenantId, nameof(Publication), publication.Id, AuditAction.StatusChange, new Dictionary<string, object?> { ["from"] = "Scheduled", ["to"] = "Cancelled" }, ct);
        return ToDto(publication, await NumberAsync(tenantId, publication.VersionId, ct));
    }

    public async Task<PublicationDto> RetryAsync(Guid tenantId, Guid publicationId, CancellationToken ct = default)
    {
        var publication = await FindAsync(tenantId, publicationId, ct);
        if (publication.Status != PublicationStatus.Failed)
            throw new ConflictException("Only a publication that failed can be tried again.");
        if (!string.IsNullOrEmpty(publication.ExternalPostId))
            throw new ConflictException("YouTube already has this video, so it will not be uploaded again.");

        var version = await _db.ContentVersions.AsNoTracking().FirstAsync(v => v.Id == publication.VersionId && v.TenantId == tenantId, ct);
        var render = await _db.RenderJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == publication.RenderJobId && j.TenantId == tenantId, ct);
        var problem = await PublishGate.ProblemAsync(_db, tenantId, version, render, ct);
        if (problem is not null)
            throw new ConflictException(problem);
        var connection = await _db.SocialConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == publication.ConnectionId && c.TenantId == tenantId, ct)
            ?? throw new ConflictException("That YouTube account is no longer connected. Connect it again, then publish again.");
        if (connection.Status != SocialConnectionStatus.Connected)
            throw new ConflictException($"Your YouTube account \"{connection.AccountName}\" needs to be connected again first.");

        publication.Status = PublicationStatus.Scheduled;
        publication.Attempts = 0;
        publication.NextRetryAtUtc = null;
        publication.LastError = null;
        publication.ScheduledAtUtc = null;
        publication.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(tenantId, nameof(Publication), publication.Id, AuditAction.StatusChange, new Dictionary<string, object?> { ["from"] = "Failed", ["to"] = "Scheduled" }, ct);
        _queue.Enqueue(publication.Id, tenantId, null);
        return ToDto(publication, version.Number);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    /// <summary>The tenant's words, then the credit lines of the stock photos used (taken from the pictures themselves, never typed).</summary>
    private async Task<string?> BuildDescriptionAsync(Guid tenantId, Guid versionId, string? description, CancellationToken ct)
    {
        var body = Clean(description, PublishingLimits.MaxDescription);
        var ids = await _db.ContentScenes.AsNoTracking().Where(s => s.TenantId == tenantId && s.VersionId == versionId && s.ChosenAssetId != null).Select(s => s.ChosenAssetId!.Value).Distinct().ToListAsync(ct);
        var credits = (await _db.StudioAssets.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id) && a.Attribution != null).Select(a => a.Attribution!).ToListAsync(ct)).Distinct().ToList();
        var text = credits.Count == 0 ? body : (body is null ? string.Empty : body + "\n\n") + "Credits:\n" + string.Join("\n", credits.Select(c => "- " + c));
        return Clean(text, 5000);
    }

    private void EnsureConfigured()
    {
        if (!_youtube.IsConfigured)
            throw new ConflictException("Publishing to YouTube is not set up on this platform yet. Ask the platform administrator.");
    }

    private static void EnsureValidRedirect(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw Invalid("redirectUri", "The redirect address must be an https address.");
    }

    /// <summary>The state must be one this server issued, for this tenant, and not expired - otherwise a login started elsewhere could connect someone else's channel here.</summary>
    private void EnsureStateIsOurs(Guid tenantId, string state)
    {
        var plain = string.IsNullOrWhiteSpace(state) ? null : _protector.TryUnprotect(state);
        var parts = plain?.Split('|');
        if (parts is not { Length: 2 } || parts[0] != tenantId.ToString("N") || !long.TryParse(parts[1], out var ticks) || new DateTime(ticks, DateTimeKind.Utc) < _clock.UtcNow)
            throw Invalid("state", "This Google sign-in has expired or did not start here. Please click Connect again.");
    }

    private async Task<Publication> FindAsync(Guid tenantId, Guid publicationId, CancellationToken ct) =>
        await _db.Publications.FirstOrDefaultAsync(p => p.Id == publicationId && p.TenantId == tenantId, ct) ?? throw new NotFoundException("Publication", publicationId);

    private async Task<int> NumberAsync(Guid tenantId, Guid versionId, CancellationToken ct) =>
        await _db.ContentVersions.AsNoTracking().Where(v => v.Id == versionId && v.TenantId == tenantId).Select(v => v.Number).FirstOrDefaultAsync(ct);

    private static ConnectionDto ToDto(SocialConnection c) =>
        new(c.Id, c.Platform.ToString(), c.AccountName, c.Status.ToString(), c.LastError, c.CreatedAt, c.LastUsedAtUtc);

    public static PublicationDto ToDto(Publication p, int versionNumber) => new(
        p.Id, p.VersionId, versionNumber, p.RenderJobId, p.Platform.ToString(), p.AccountName, p.Status.ToString(), p.Title, p.Description, p.Privacy, p.ScheduledAtUtc, p.Url, p.Attempts,
        p.NextRetryAtUtc, p.LastError, p.CreatedAt, p.PublishedAtUtc);

    private static string? Clean(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
