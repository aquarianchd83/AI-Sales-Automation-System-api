namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Bound from the "WhatsApp" config section.</summary>
public class WhatsAppSettings
{
    /// <summary>"Simulated" (default, no real credentials needed) or "Meta".</summary>
    public string Provider { get; set; } = "Simulated";

    public string PhoneNumberId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v19.0";

    /// <summary>0-100. Lets the retry pipeline be exercised without needing a real Meta failure.</summary>
    public int SimulatedFailureRatePercent { get; set; } = 0;

    /// <summary>Meta App Secret, used to verify the X-Hub-Signature-256 HMAC on every inbound webhook
    /// POST before any of its content is trusted. Phase 4.</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>The value configured in Meta's webhook setup - echoed back on the GET verification
    /// handshake to prove this endpoint belongs to the same person who registered the webhook URL.</summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;
}
