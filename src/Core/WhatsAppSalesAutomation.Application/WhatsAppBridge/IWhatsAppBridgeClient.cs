using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.WhatsAppBridge;

/// <summary>Where the self-hosted bridge (Evolution API) lives and the platform key that manages it.</summary>
public record BridgeEndpoint(string BaseUrl, string ApiKey);

/// <summary>A QR pairing code. Temporary authentication material: handed to the UI and never stored or logged.</summary>
public record BridgeQr(string? Base64, string? Code);

/// <param name="InstanceToken">The bridge's own per-instance token. A secret: stored encrypted, never logged.</param>
/// <param name="State">The bridge's raw state string ("connecting", "open", ...).</param>
public record BridgeInstance(string InstanceName, string? InstanceToken, string State, BridgeQr? Qr);

/// <summary>A call to the bridge failed in a way the caller can show an operator (unreachable, rejected key, refused request).</summary>
public class WhatsAppBridgeException : Exception
{
    public WhatsAppBridgeException(string message) : base(message) { }
}

/// <summary>
/// The QR-code bridge's wire protocol, kept behind one interface so nothing above Infrastructure knows Evolution API's
/// endpoints or payloads. Shapes were verified against a running Evolution API v2.3.7 (see docs/whatsapp-provider).
/// </summary>
public interface IWhatsAppBridgeClient
{
    /// <summary>Proves the address is reachable and the key accepted. Throws <see cref="WhatsAppBridgeException"/> otherwise.</summary>
    Task PingAsync(BridgeEndpoint endpoint, CancellationToken cancellationToken = default);

    /// <summary>Creates a linked-device instance and starts pairing; the result carries the first QR.</summary>
    Task<BridgeInstance> CreateInstanceAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default);

    /// <summary>A fresh QR for an instance that is not connected yet; null when the bridge returned none.</summary>
    Task<BridgeQr?> ConnectAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default);

    /// <summary>The bridge's raw connection state, or null when the instance does not exist.</summary>
    Task<string?> GetStateAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default);

    /// <summary>Points the instance's events at <paramref name="url"/>, sending <paramref name="secretHeaderName"/>: <paramref name="secret"/> with each call.</summary>
    Task ConfigureWebhookAsync(BridgeEndpoint endpoint, string instanceName, string url, string secretHeaderName, string secret, CancellationToken cancellationToken = default);

    Task LogoutAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default);

    /// <summary>Best effort: on an open instance the bridge answers an error body yet still drops the connection, so failures are swallowed.</summary>
    Task RestartAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default);

    /// <summary>Removes the instance. A missing instance is not an error.</summary>
    Task DeleteAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default);

    /// <summary>Plain text only - the one message kind verified end to end. A failure is a result, not an exception, like Meta's client.</summary>
    Task<WhatsAppSendResult> SendTextAsync(BridgeEndpoint endpoint, string instanceName, string toPhoneNumberE164, string text, CancellationToken cancellationToken = default);
}
