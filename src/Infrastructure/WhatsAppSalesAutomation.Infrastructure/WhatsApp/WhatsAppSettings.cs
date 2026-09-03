namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Bound from the "WhatsApp" config section.</summary>
public class WhatsAppSettings
{
    /// <summary>"Simulated" (default, no real credentials needed) or "Meta".</summary>
    public string Provider { get; set; } = "Simulated";

    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>The WhatsApp Business Account (WABA) id - distinct from PhoneNumberId. Template
    /// review/status lives at the WABA level (one WABA can own several phone numbers), so
    /// MessageTemplateSyncJob's Meta call is keyed off this, not PhoneNumberId.</summary>
    public string WhatsAppBusinessAccountId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v19.0";

    /// <summary>Base URL for the Cloud API, with or without a trailing slash - MetaWhatsAppCloudApiClient
    /// normalizes it. Configurable rather than hardcoded so a Meta API domain change, a regional
    /// endpoint, or (for testing) a mock server can be pointed at without a code change.</summary>
    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/";

    /// <summary>0-100. Lets the retry pipeline be exercised without needing a real Meta failure.</summary>
    public int SimulatedFailureRatePercent { get; set; } = 0;

    /// <summary>Meta App Secret, used to verify the X-Hub-Signature-256 HMAC on every inbound webhook
    /// POST before any of its content is trusted (Phase 4), and as the client_secret half of the
    /// token-refresh exchange (Phase 6) - genuinely confidential, unlike AppId below.</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>The Meta App's numeric App ID - the client_id half of the OAuth token-exchange call
    /// WhatsAppTokenRefreshService uses to keep AccessToken from expiring. Not confidential (Meta
    /// itself treats App IDs as public - they appear client-side in Meta SDKs/Login buttons), unlike
    /// AppSecret.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>The value configured in Meta's webhook setup - echoed back on the GET verification
    /// handshake to prove this endpoint belongs to the same person who registered the webhook URL.</summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;
}
