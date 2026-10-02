using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Media;

/// <summary>Which public link a media file is reached at. A file the tenant added by link keeps THEIR address; one we
/// stored is rebuilt from the current MediaStorage configuration (the saved Url goes stale when it changes).</summary>
public static class MediaAssetLinks
{
    public const string ExternalProvider = "External";

    public static string PublicUrl(string storageProvider, string storageKey, string storedUrl, IMediaStorageService? storage) =>
        storageProvider == ExternalProvider || storage is null ? storedUrl : storage.GetPublicUrl(storageKey);
}
