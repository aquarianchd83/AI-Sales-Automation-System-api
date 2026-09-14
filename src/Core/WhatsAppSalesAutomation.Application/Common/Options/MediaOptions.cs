namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>Bound from the "Media" config section. Defaults mirror WhatsApp's own media limits
/// (16 MB, images/video only) so a file that would be rejected at send time is rejected at upload
/// time instead - the real default values live in appsettings.json's own "Media" section, not here.
/// <see cref="AllowedContentTypes"/> deliberately starts empty (not the actual default list) - see
/// that property's own doc comment for why.</summary>
public class MediaOptions
{
    public long MaxSizeBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Must default to an empty array, not the real "image/jpeg, image/png, ..." default
    /// list: ConfigurationBinder appends config-bound array items onto an already-non-null array
    /// property rather than replacing it, so a non-empty default here would come back from
    /// IOptionsSnapshot&lt;MediaOptions&gt; with every item duplicated (the config source and this
    /// default both contributing the same 5 entries). appsettings.json's own "Media:AllowedContentTypes"
    /// array is what actually supplies the real default for a fresh install with no DB override -
    /// this empty array only matters if that JSON section were ever removed too.</summary>
    public string[] AllowedContentTypes { get; set; } = Array.Empty<string>();
}
