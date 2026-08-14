namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Bound from the "Media" config section. Defaults mirror WhatsApp's own media limits
/// (16 MB, images/video only) so a file that would be rejected at send time is rejected at upload
/// time instead.</summary>
public class MediaOptions
{
    public long MaxSizeBytes { get; set; } = 16 * 1024 * 1024;

    public string[] AllowedContentTypes { get; set; } =
    {
        "image/jpeg", "image/png", "image/webp", "video/mp4", "video/3gpp"
    };
}
