namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Bound from the "PlatformWhatsApp" config section - in practice the AppSettings table, edited on the Platform Admin Console's WhatsApp page.
/// The number the PLATFORM sends tenant notices from; unrelated to any tenant's own number.</summary>
public class PlatformWhatsAppOptions
{
    /// <summary>The pause switch. Defaults to on: an unset value must not silently turn a configured number off.</summary>
    public bool Enabled { get; set; } = true;

    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>Where the platform's own templates live (and what Meta reviews them under).</summary>
    public string WhatsAppBusinessAccountId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v19.0";

    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(PhoneNumberId) && !string.IsNullOrWhiteSpace(AccessToken);
}
