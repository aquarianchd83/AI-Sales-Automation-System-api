using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

/// <summary>
/// Sends one publication to YouTube, in the tenant's scope. Before anything leaves, the same gate as at request time runs again (approved,
/// unchanged, the matching finished video, the account still connected). A publication that already has YouTube's id for its video is never
/// uploaded again. A temporary problem before anything was sent is retried a few times with growing waits; a refusal that will not change (quota,
/// policy, a rejected file) fails at once with the reason; a lost connection <i>while sending</i> is never retried automatically, because the
/// video may already be there - the tenant is told to look at their channel first; a sign-in that no longer works marks the account as needing
/// to be connected again. No token ever appears in a message or a log.
/// </summary>
public sealed class PublicationProcessor : IPublicationProcessor
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly IYouTubeClient _youtube;
    private readonly ITenantDocumentStore _store;
    private readonly IPublicationQueue _queue;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<PublicationProcessor> _logger;

    public PublicationProcessor(
        IApplicationDbContext db, ISecretProtector protector, IYouTubeClient youtube, ITenantDocumentStore store, IPublicationQueue queue, IDateTimeProvider clock,
        ILogger<PublicationProcessor> logger)
    {
        _db = db;
        _protector = protector;
        _youtube = youtube;
        _store = store;
        _queue = queue;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(Guid publicationId, CancellationToken ct = default)
    {
        var publication = await _db.Publications.FirstOrDefaultAsync(p => p.Id == publicationId, ct);
        if (publication is null || publication.Status != PublicationStatus.Scheduled)
            return; // cancelled, already sent or already running: a repeated message must not send twice

        var now = _clock.UtcNow;
        if (publication.ScheduledAtUtc is { } due && due > now.AddSeconds(5))
        {
            _queue.Enqueue(publication.Id, publication.TenantId, due - now); // woken early: wait for the real time
            return;
        }

        if (!string.IsNullOrEmpty(publication.ExternalPostId))
        {
            // YouTube already has it (a result that was not saved the first time): record it, never upload again.
            publication.Status = PublicationStatus.Published;
            publication.PublishedAtUtc ??= now;
            await SaveAsync(publication, ct);
            return;
        }

        var version = await _db.ContentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == publication.VersionId, ct);
        var render = await _db.RenderJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == publication.RenderJobId, ct);
        var problem = version is null ? "The script no longer exists." : await PublishGate.ProblemAsync(_db, publication.TenantId, version, render, ct);
        if (problem is null && version!.ApprovedHash != publication.ApprovedHash)
            problem = "The approved script is not the one this was requested for.";
        if (problem is not null)
        {
            await FailAsync(publication, problem, ct);
            return;
        }

        var connection = await _db.SocialConnections.FirstOrDefaultAsync(c => c.Id == publication.ConnectionId, ct);
        if (connection is null)
        {
            await FailAsync(publication, "The YouTube account was disconnected, so nothing was sent.", ct);
            return;
        }

        if (connection.Status != SocialConnectionStatus.Connected)
        {
            await FailAsync(publication, $"Your YouTube account \"{connection.AccountName}\" needs to be connected again. Connect it, then try again.", ct);
            return;
        }

        publication.Status = PublicationStatus.Uploading;
        publication.Attempts++;
        publication.StartedAtUtc = now;
        publication.LastError = null;
        publication.NextRetryAtUtc = null;
        await SaveAsync(publication, ct);

        try
        {
            var accessToken = await FreshAccessTokenAsync(connection, ct);
            var key = render!.OutputStorageKey!;
            if (!key.Contains(_store.FolderOf(publication.TenantId), StringComparison.Ordinal))
                throw new SocialProviderException("The video file could not be found.", SocialFailure.Permanent);

            await using var video = await _store.OpenReadAsync(key, ct);
            var id = await _youtube.UploadAsync(
                accessToken, new YouTubeUploadRequest(publication.Title, publication.Description ?? string.Empty, publication.Privacy, publication.DisclosesSyntheticMedia), video, render.OutputBytes, ct);

            publication.ExternalPostId = id;
            publication.Url = $"https://www.youtube.com/watch?v={id}";
            publication.Status = PublicationStatus.Published;
            publication.PublishedAtUtc = _clock.UtcNow;
            publication.LastError = null;
            connection.LastUsedAtUtc = _clock.UtcNow;
            await SaveAsync(publication, ct);
        }
        catch (SocialProviderException ex)
        {
            await HandleFailureAsync(publication, connection, ex, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or StorageUnavailableException)
        {
            // Something went wrong we have no sentence for. Whether the video got there is unknown, so it is not retried by itself.
            _logger.LogWarning(ex, "Publication {PublicationId} lost contact.", publication.Id);
            await FailAsync(publication, "Contact was lost while the video was being sent, so it may or may not be on YouTube. Look at your channel before trying again.", ct);
        }
    }

    private async Task HandleFailureAsync(Publication publication, SocialConnection connection, SocialProviderException ex, CancellationToken ct)
    {
        switch (ex.Failure)
        {
            case SocialFailure.Transient when publication.Attempts < PublishingLimits.MaxAttempts:
                var wait = PublishingLimits.Backoff[Math.Min(publication.Attempts - 1, PublishingLimits.Backoff.Length - 1)];
                publication.Status = PublicationStatus.Scheduled;
                publication.NextRetryAtUtc = _clock.UtcNow + wait;
                publication.LastError = $"{ex.Message} Trying again automatically in {(int)wait.TotalMinutes} minutes.";
                await SaveAsync(publication, ct);
                _queue.Enqueue(publication.Id, publication.TenantId, wait);
                return;

            case SocialFailure.Transient:
                await FailAsync(publication, $"{ex.Message} It was tried {publication.Attempts} times; try again later.", ct);
                return;

            case SocialFailure.NeedsReconnect:
                connection.Status = SocialConnectionStatus.NeedsReconnect;
                connection.LastError = ex.Message;
                connection.UpdatedAt = _clock.UtcNow;
                await FailAsync(publication, ex.Message, ct);
                return;

            default:
                await FailAsync(publication, ex.Message, ct);
                return;
        }
    }

    /// <summary>The saved access token, renewed first when it is about to run out.</summary>
    private Task<string> FreshAccessTokenAsync(SocialConnection connection, CancellationToken ct) =>
        SocialTokens.FreshAccessTokenAsync(_db, _protector, _youtube, _clock, connection, ct);

    private async Task FailAsync(Publication publication, string message, CancellationToken ct)
    {
        publication.Status = PublicationStatus.Failed;
        publication.NextRetryAtUtc = null;
        publication.LastError = message.Length <= 600 ? message : message[..600];
        await SaveAsync(publication, ct);
    }

    private async Task SaveAsync(Publication publication, CancellationToken ct)
    {
        publication.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
