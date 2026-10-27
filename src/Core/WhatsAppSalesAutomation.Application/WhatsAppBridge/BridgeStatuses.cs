namespace WhatsAppSalesAutomation.Application.WhatsAppBridge;

/// <summary>The application's own connection statuses for a tenant's QR channel (strings, like <c>WhatsAppProviders</c>). Provider
/// states are mapped in <see cref="FromProviderState"/>; a state the bridge has not been seen to send maps to nothing rather than a guess.</summary>
public static class BridgeStatuses
{
    public const string NotConnected = "NotConnected";
    public const string ConnectionInitiated = "ConnectionInitiated";
    public const string QrCodeAvailable = "QrCodeAvailable";
    public const string AwaitingAuthentication = "AwaitingAuthentication";
    public const string Connected = "Connected";
    public const string Disconnected = "Disconnected";
    public const string Reconnecting = "Reconnecting";
    public const string ConnectionFailed = "ConnectionFailed";
    public const string Disabled = "Disabled";

    /// <summary>Only "open" and "connecting" were observed from Evolution API v2.3.7. "close" is the documented disconnect state but was
    /// not captured live - it is mapped, flagged here, and an unexpected-disconnect capture should confirm it.</summary>
    public static string? FromProviderState(string? providerState, string? previous) => providerState?.Trim().ToLowerInvariant() switch
    {
        "open" => Connected,
        // Connecting after having been connected is the bridge re-establishing the session, not a fresh pairing.
        "connecting" => previous == Connected ? Reconnecting : AwaitingAuthentication,
        "close" => Disconnected,
        _ => null
    };

    /// <summary>Whether sends may go out. Everything but Connected pauses sending - never a fallback to another provider.</summary>
    public static bool CanSend(string? status) => status == Connected;
}
