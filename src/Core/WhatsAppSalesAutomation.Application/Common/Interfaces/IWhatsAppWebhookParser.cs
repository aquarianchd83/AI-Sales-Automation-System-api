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

/// <summary>
/// <paramref name="PhoneNumberId"/> is Meta's <c>metadata.phone_number_id</c> - the WABA phone number
/// this delivery was addressed to, present on every real Meta webhook payload regardless of whether it
/// carries messages, statuses, or both. Null only for a malformed/unrecognised payload; multi-tenant
/// webhook routing (WebhooksController.Receive) depends on this being populated to know which tenant
/// the delivery belongs to before anything else about it is trusted.
/// </summary>
public record WhatsAppWebhookParseResult(
    IReadOnlyList<InboundWhatsAppMessage> Messages,
    IReadOnlyList<WhatsAppStatusUpdate> Statuses,
    string? PhoneNumberId = null);

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

/// <summary>
/// <paramref name="Status"/> is Meta's raw status string: "sent", "delivered", "read" or "failed".
/// <paramref name="FailureReason"/> is only ever non-null when <paramref name="Status"/> is "failed" -
/// Meta's human-readable explanation (its numeric error code prefixed, e.g. "[131047] Re-engagement
/// message ...") for why the message could not be delivered.
/// </summary>
public record WhatsAppStatusUpdate(
    string WhatsAppMessageId,
    string Status,
    DateTime Timestamp,
    string? FailureReason = null);
