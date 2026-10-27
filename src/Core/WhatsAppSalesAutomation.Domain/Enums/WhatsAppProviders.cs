namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>
/// Which messaging integration carried a <c>Message</c>. A plain string rather than an enum, like
/// <c>WhatsAppConnectionModes</c>, so adding a provider never needs a column conversion. Provider ids
/// (<c>Message.WhatsAppMessageId</c>) are only unique within one provider, so every lookup by that id
/// must be scoped by one of these.
/// </summary>
public static class WhatsAppProviders
{
    public const string Meta = "Meta";

    /// <summary>The QR-code (linked device) bridge - not yet wired to anything; reserved so the unique index is already scoped.</summary>
    public const string QrBridge = "QrBridge";
}
