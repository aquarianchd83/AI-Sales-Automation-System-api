namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Bound from the "WhatsAppProvider" config section - in practice the AppSettings table, edited on the Platform Admin Console.
/// Read through IOptionsSnapshot, so a change applies from the next request with no restart.</summary>
public class WhatsAppProviderOptions
{
    /// <summary>"Meta" (default) or "QrBridge".</summary>
    public string Active { get; set; } = "Meta";

    /// <summary>Per-tenant choices as "tenantId=Provider"; a tenant listed here ignores <see cref="Active"/>.</summary>
    public List<string> TenantOverrides { get; set; } = new();
}

/// <summary>Bound from the "WhatsAppBridge" config section (see <c>WhatsAppBridgeSettingsService</c>).</summary>
public class WhatsAppBridgeOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Decrypted by the settings configuration provider; never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string WebhookBaseUrl { get; set; } = string.Empty;

    /// <summary>While non-empty, only these tenants use the bridge when it is the active provider; the rest stay on Meta. Empty: every tenant.</summary>
    public List<Guid> PilotTenantIds { get; set; } = new();
}
