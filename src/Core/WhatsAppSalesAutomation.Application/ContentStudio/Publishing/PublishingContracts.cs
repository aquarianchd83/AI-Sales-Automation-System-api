using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

/// <summary>The platform's Google OAuth client and its publishing limit (Platform &gt; Configuration &gt; ContentStudio).</summary>
public class SocialOptions
{
    public string GoogleClientId { get; set; } = string.Empty;

    public string GoogleClientSecret { get; set; } = string.Empty;

    /// <summary>Most videos the whole platform sends to YouTube in 24 hours.</summary>
    public int MaxPublishesPerDay { get; set; } = 5;

    public bool YouTubeConfigured => !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    public int DailyLimit => Math.Clamp(MaxPublishesPerDay, 1, 1000);
}

public static class PublishingLimits
{
    public const int MaxAttempts = 4;
    public static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30) };
    public static readonly TimeSpan MaxScheduleAhead = TimeSpan.FromDays(90);
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(2);
    public static readonly string[] Privacies = { "private", "unlisted", "public" };
    public const int MaxTitle = 100;
    public const int MaxDescription = 4800;
}

/// <summary>How a platform's refusal should be treated.</summary>
public enum SocialFailure
{
    /// <summary>A temporary problem before anything was posted (the service was busy or unreachable): trying again is safe.</summary>
    Transient = 0,

    /// <summary>The platform said no and will say no again (quota, policy, rejected file): do not retry.</summary>
    Permanent = 1,

    /// <summary>Contact was lost while the video was being sent, so it may or may not have been posted: never retried automatically.</summary>
    Ambiguous = 2,

    /// <summary>The saved sign-in no longer works: the tenant has to connect again.</summary>
    NeedsReconnect = 3
}

/// <summary>A failure of a publishing platform whose message is safe to show the tenant.</summary>
public sealed class SocialProviderException : Exception
{
    public SocialProviderException(string message, SocialFailure failure, Exception? inner = null) : base(message, inner) => Failure = failure;

    public SocialFailure Failure { get; }
}

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTime ExpiresAtUtc, string Scopes);

public sealed record YouTubeChannel(string Id, string Title);

/// <summary>How a video is doing. A count the owner hides (likes, comments) is reported as 0.</summary>
public sealed record VideoStatistics(long Views, long Likes, long Comments);

/// <summary>How long finished files and dead sign-ins are kept (Platform &gt; Configuration &gt; ContentStudio). 0 keeps them for ever; the minimum is 7 days.</summary>
public class StudioRetentionOptions
{
    public const int MinimumDays = 7;

    public int VideoDays { get; set; } = 90;

    public int DeadSignInDays { get; set; } = 90;

    public static int? Effective(int days) => days <= 0 ? null : Math.Max(days, MinimumDays);
}

/// <param name="SyntheticMedia">Tells YouTube the video contains realistic altered or synthetic content (here: a synthetic voice). Always true.</param>
public sealed record YouTubeUploadRequest(string Title, string Description, string Privacy, bool SyntheticMedia);

/// <summary>YouTube as a place to publish. Provider-agnostic so Instagram can sit beside it later.</summary>
public interface IYouTubeClient
{
    bool IsConfigured { get; }

    string BuildLoginUrl(string redirectUri, string state);

    Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default);

    Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task<YouTubeChannel?> GetChannelAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <returns>YouTube's id for the new video.</returns>
    Task<string> UploadAsync(string accessToken, YouTubeUploadRequest request, Stream video, long length, CancellationToken cancellationToken = default);

    Task RevokeAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Views, likes and comments for up to 50 videos in one call (one unit of Google's quota). A video the platform no longer returns is simply absent from the result.</summary>
    Task<IReadOnlyDictionary<string, VideoStatistics>> GetVideoStatisticsAsync(string accessToken, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);
}

public interface IPublicationQueue
{
    /// <param name="delay">Null: now.</param>
    void Enqueue(Guid publicationId, Guid tenantId, TimeSpan? delay);
}

// ----- what the tenant sees ---------------------------------------------------------------------------------------------------------

public sealed record ConnectUrlDto(string Url);

public sealed record CompleteConnectRequest(string Code, string State, string RedirectUri);

public sealed record ConnectionDto(Guid Id, string Platform, string AccountName, string Status, string? LastError, DateTime ConnectedAt, DateTime? LastUsedAt);

/// <param name="Privacy">"private" (default), "unlisted" or "public".</param>
/// <param name="ScheduledAtUtc">Null: as soon as possible.</param>
/// <param name="IdempotencyKey">One per click: the same key twice is the same publication.</param>
public sealed record PublishVideoRequest(Guid RenderJobId, Guid ConnectionId, string Title, string? Description, string? Privacy, DateTime? ScheduledAtUtc, string? IdempotencyKey);

public sealed record PublicationDto(
    Guid Id, Guid VersionId, int VersionNumber, Guid RenderJobId, string Platform, string AccountName, string Status, string Title, string? Description, string Privacy,
    DateTime? ScheduledAtUtc, string? Url, int Attempts, DateTime? NextRetryAtUtc, string? LastError, DateTime CreatedAt, DateTime? PublishedAtUtc);

public interface IPublishingService
{
    /// <summary>Whether the platform has the Google OAuth client set up.</summary>
    bool YouTubeAvailable { get; }

    Task<ConnectUrlDto> GetConnectUrlAsync(Guid tenantId, string redirectUri, CancellationToken cancellationToken = default);

    Task<ConnectionDto> ConnectAsync(Guid tenantId, CompleteConnectRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Revokes the platform's access, deletes the saved tokens, and cancels what was still waiting to be sent through it.</summary>
    Task DisconnectAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken = default);

    Task<PublicationDto> PublishAsync(Guid tenantId, Guid versionId, PublishVideoRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicationDto>> ListAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);

    Task<PublicationDto> CancelAsync(Guid tenantId, Guid publicationId, CancellationToken cancellationToken = default);

    /// <summary>Tries a failed publication again. Refused if the platform already has the video.</summary>
    Task<PublicationDto> RetryAsync(Guid tenantId, Guid publicationId, CancellationToken cancellationToken = default);
}

public interface IPublicationProcessor
{
    Task RunAsync(Guid publicationId, CancellationToken cancellationToken = default);
}
