using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// A tenant's sign-in to a place it can publish to (today a YouTube channel). The platform's tokens are kept encrypted, are never returned by
/// any endpoint and never logged; disconnecting asks the platform to revoke them and deletes them here.
/// </summary>
public class SocialConnection : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public SocialPlatform Platform { get; set; }

    /// <summary>The platform's id for the account (a YouTube channel id).</summary>
    public string ExternalAccountId { get; set; } = string.Empty;

    /// <summary>What the tenant knows it as (the channel's name) - shown wherever they choose where to publish.</summary>
    public string AccountName { get; set; } = string.Empty;

    public SocialConnectionStatus Status { get; set; } = SocialConnectionStatus.Connected;

    public string EncryptedAccessToken { get; set; } = string.Empty;

    public string EncryptedRefreshToken { get; set; } = string.Empty;

    public DateTime TokenExpiresAtUtc { get; set; }

    /// <summary>The permissions the tenant granted, as the platform reported them.</summary>
    public string Scopes { get; set; } = string.Empty;

    public Guid? ConnectedBy { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>Why the connection needs attention, in a sentence the tenant can act on.</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// One request to publish an approved video to a connected account. It records exactly what was approved (the fingerprint), the video that was
/// made from it, where it goes, with what title, when, and how it ended - and the platform's own id for the post once there is one. A
/// publication that has a post id is never uploaded again.
/// </summary>
public class Publication : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid VersionId { get; set; }

    public Guid RenderJobId { get; set; }

    public Guid ConnectionId { get; set; }

    public SocialPlatform Platform { get; set; }

    /// <summary>The account's name when it was chosen, kept so the record still reads after the account is disconnected.</summary>
    public string AccountName { get; set; } = string.Empty;

    public PublicationStatus Status { get; set; } = PublicationStatus.Scheduled;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>"private", "unlisted" or "public".</summary>
    public string Privacy { get; set; } = "private";

    /// <summary>Always true: the narration is a synthetic voice, and the platform is told so.</summary>
    public bool DisclosesSyntheticMedia { get; set; } = true;

    /// <summary>When to send it (UTC); null means as soon as possible.</summary>
    public DateTime? ScheduledAtUtc { get; set; }

    public string? ExternalPostId { get; set; }

    public string? Url { get; set; }

    public int Attempts { get; set; }

    /// <summary>When the next automatic try is due, after a temporary problem.</summary>
    public DateTime? NextRetryAtUtc { get; set; }

    /// <summary>One per real-world request: the same key twice is the same publication, never two posts.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>The approved script's fingerprint this was published against.</summary>
    public string ApprovedHash { get; set; } = string.Empty;

    public string? LastError { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>When the platform was last asked how the post is doing.</summary>
    public DateTime? MetricsRefreshedAtUtc { get; set; }

    /// <summary>Set when the platform stopped returning the post (deleted, or made unavailable there). Cleared if it comes back.</summary>
    public DateTime? MissingOnPlatformSinceUtc { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>How a published post was doing at one moment. Rows are only ever added: the series is the history.</summary>
public class PublicationMetricSnapshot : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid PublicationId { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public long Views { get; set; }

    public long Likes { get; set; }

    public long Comments { get; set; }
}
