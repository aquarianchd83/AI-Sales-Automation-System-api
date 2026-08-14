namespace WhatsAppSalesAutomation.Infrastructure.Storage;

/// <summary>Bound from the "MediaStorage" config section.</summary>
public class LocalMediaStorageSettings
{
    /// <summary>Relative to the API's content root, or absolute.</summary>
    public string RootPath { get; set; } = "App_Data/media";

    /// <summary>URL prefix the API serves this folder under - see Program.cs's static file mapping.</summary>
    public string PublicBasePath { get; set; } = "/media";

    /// <summary>
    /// Scheme+host to prepend, e.g. "https://api.example.com". Empty in local dev, where the
    /// resulting relative URL is fine for browsing but NOT reachable by Meta's servers to fetch
    /// template header media - that needs a real public URL, so set this before using the "Meta"
    /// WhatsApp provider with media-bearing templates.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
}
