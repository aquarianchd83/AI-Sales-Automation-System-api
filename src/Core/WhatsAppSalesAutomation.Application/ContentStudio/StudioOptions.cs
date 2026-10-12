using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

/// <summary>
/// Bound from the "ContentStudio" config section, which the Platform Admin Console edits (DB-backed AppSettings).
/// Platform-wide: a tenant never sees a provider or a bucket. Off by default, so shipping this changes nothing for
/// anyone until the operator switches it on.
/// </summary>
public class StudioOptions
{
    /// <summary>Master switch for the whole module.</summary>
    public bool Enabled { get; set; }

    /// <summary>"PublicBucket" (default: the platform's existing public bucket, reached by unguessable keys) or
    /// "SignedUrls". Read on every request, so flipping it needs no restart and no data migration - the key layout is
    /// the same in both modes.</summary>
    public string StorageMode { get; set; } = nameof(StudioStorageMode.PublicBucket);

    /// <summary>Folder every studio file lives under, beside the Media Library's own prefix.</summary>
    public string StorageKeyPrefix { get; set; } = "studio";

    /// <summary>How long a signed address stays valid, in minutes. Only used in SignedUrls mode.</summary>
    public int SignedUrlMinutes { get; set; } = 60;

    public StudioStorageMode ResolvedStorageMode =>
        Enum.TryParse<StudioStorageMode>(StorageMode?.Trim(), ignoreCase: true, out var mode) ? mode : StudioStorageMode.PublicBucket;
}
