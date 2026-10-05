namespace WhatsAppSalesAutomation.Application.SocialAds;

public record SocialAdAccountDto(string Id, string Name, string CurrencyCode);

/// <summary>
/// What the Settings page shows. <paramref name="Status"/> is "NotConnected", "PendingAccountSelection", "Connected"
/// or "NeedsReconnect". <paramref name="IsAvailable"/> is false until the platform has a Meta App configured, in
/// which case nothing can be connected yet.
/// </summary>
public record SocialAdsStatusDto(
    bool IsAvailable,
    string Status,
    string? AdAccountId,
    string? AdAccountName,
    string? CurrencyCode,
    DateTime? ConnectedAt,
    DateTime? LastSyncedAt,
    string? LastSyncError,
    DateTime? TokenExpiresAt,
    IReadOnlyList<SocialAdAccountDto> Accounts);

public record SocialAdsConnectUrlDto(string Url);

/// <summary><paramref name="Code"/> and <paramref name="State"/> are what Facebook sent back to the redirect page.</summary>
public record CompleteSocialAdsConnectRequest(string Code, string State, string RedirectUri);

public record SelectSocialAdAccountRequest(string AdAccountId);

/// <summary>A month's ad spend typed in by hand, for a tenant with no ad account to connect. <paramref name="Month"/>
/// is any date in the month.</summary>
public record SaveManualAdSpendRequest(DateTime Month, decimal Amount);

public record ManualAdSpendDto(DateTime Month, decimal Amount, string CurrencyCode);
