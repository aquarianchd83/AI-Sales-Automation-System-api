namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One tenant's row on the WhatsApp Connections screen (spec item #5) - connection identity/
/// status from <c>TenantWhatsAppConfig</c> plus a rolling 24h webhook delivery health roll-up from
/// <c>WebhookEvent</c>. See <c>TenantWhatsAppConnectionSummary</c>'s own doc comment for why there is
/// no token-expiry column - BYO-WABA tenant tokens aren't tracked by any refresh mechanism today.</summary>
public record PlatformWhatsAppConnectionDto(
    Guid TenantId,
    string TenantName,
    string? PhoneNumberId,
    string? WhatsAppBusinessAccountId,
    bool IsConnected,
    DateTime? ConfigUpdatedAtUtc,
    int WebhookEventsLast24h,
    int WebhookFailuresLast24h,
    DateTime? LastWebhookReceivedAtUtc);
