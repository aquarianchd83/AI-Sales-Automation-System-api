namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// Turns Meta's raw webhook JSON into plain data the Application layer can work with, without that
/// layer knowing anything about the wire format - implemented in Infrastructure, mirroring how
/// ICustomerImportService keeps CSV/Excel specifics out of Application. A malformed or unrecognised
/// payload results in an empty result, not an exception - one bad delivery should not be indistinguishable
/// from an infrastructure fault.
/// </summary>
public interface IWhatsAppWebhookParser
{
    WhatsAppWebhookParseResult Parse(string rawPayload);
}

public record WhatsAppWebhookParseResult(
    IReadOnlyList<InboundWhatsAppMessage> Messages,
    IReadOnlyList<WhatsAppStatusUpdate> Statuses);

/// <summary>
/// <paramref name="FromPhone"/> is Meta's raw digit string (e.g. "919876543210"), not yet run through
/// PhoneNumberNormalizer - normalization is Application business logic the parser deliberately does
/// not duplicate; InboundWebhookProcessor is what actually calls PhoneNumberNormalizer.TryNormalize
/// on it. <paramref name="MessageType"/> is Meta's raw type string ("text", "image", ...) - Phase 4
/// only meaningfully processes "text"; anything else is still recorded but with a null TextBody.
/// </summary>
public record InboundWhatsAppMessage(
    string WhatsAppMessageId,
    string FromPhone,
    string? ContactName,
    DateTime Timestamp,
    string MessageType,
    string? TextBody);

/// <summary><paramref name="Status"/> is Meta's raw status string: "sent", "delivered", "read" or "failed".</summary>
public record WhatsAppStatusUpdate(
    string WhatsAppMessageId,
    string Status,
    DateTime Timestamp);
