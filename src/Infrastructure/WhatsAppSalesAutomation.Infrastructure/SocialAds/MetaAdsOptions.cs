namespace WhatsAppSalesAutomation.Infrastructure.SocialAds;

/// <summary>
/// Bound from the "MetaAds" config section (the AppSettings table, like the other platform settings). The App ID
/// and secret are optional here: when empty, the Meta App already set up for WhatsApp ("WhatsApp:AppId" and
/// "WhatsApp:AppSecret") is reused, so a platform with one Meta App needs nothing extra.
/// </summary>
public class MetaAdsOptions
{
    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v21.0";

    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/";

    /// <summary>Where the browser is sent to log in; Meta serves the login dialog from www, not graph.</summary>
    public string DialogBaseUrl { get; set; } = "https://www.facebook.com/";
}
