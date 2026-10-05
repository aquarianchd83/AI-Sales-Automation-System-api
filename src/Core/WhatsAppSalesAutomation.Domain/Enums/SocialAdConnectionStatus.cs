namespace WhatsAppSalesAutomation.Domain.Enums;

public enum SocialAdConnectionStatus
{
    /// <summary>Logged in with Facebook, but the login owns several ad accounts and none is chosen yet.</summary>
    PendingAccountSelection = 0,

    Connected = 1,

    /// <summary>Meta no longer accepts the stored token (expired, revoked, password changed). Spend already
    /// pulled stays; the tenant has to log in again for new data.</summary>
    NeedsReconnect = 2,
}
