namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>One table the product owner may let the automatic clean-up erase old rows from.</summary>
/// <param name="Key">Stable id, also what is stored in the setting and in the clean-up history.</param>
/// <param name="IsBusinessData">True for records people may rely on (customer messages). Off by default, and shown with a warning.</param>
/// <param name="FloorDays">The clean-up never keeps fewer days than this for the table, whatever the owner sets.</param>
public record CleanableTable(string Key, string Label, string Description, bool IsBusinessData, int FloorDays);

/// <summary>
/// The tables the automatic clean-up (see <c>DatabaseSizeMonitor:CleanupTables</c>) is allowed to touch - a closed list on purpose: the
/// product owner chooses among these, but cannot point the clean-up at an arbitrary table (customers, payments, billing, subscriptions...).
/// Each key has a matching delete rule in <c>DataRetentionService</c>.
/// </summary>
public static class CleanableTableCatalog
{
    public const string WebhookEvents = "WebhookEvents";
    public const string RefreshTokens = "RefreshTokens";
    public const string TenantNotifications = "TenantNotifications";
    public const string PlatformNotifications = "PlatformNotifications";
    public const string AiInteractions = "AiInteractions";
    public const string AuditLogs = "AuditLogs";
    public const string Messages = "Messages";

    public static readonly IReadOnlyList<CleanableTable> All = new List<CleanableTable>
    {
        new(WebhookEvents, "Webhook payloads", "Raw WhatsApp webhook payloads that were already processed or failed. Unprocessed ones are never deleted.", false, 0),
        new(RefreshTokens, "Expired sign-in tokens", "Refresh tokens that expired or were revoked. They can no longer be used.", false, 0),
        new(TenantNotifications, "Tenant notifications (read)", "Notifications tenants have already read. Unread ones stay.", false, 0),
        new(PlatformNotifications, "Platform notifications (read)", "Operator alerts that have been acknowledged. Unacknowledged ones stay.", false, 0),
        new(AiInteractions, "AI turn records", "What the AI saw and answered for each inbound message, with its sources.", false, 0),
        new(AuditLogs, "Audit log", "The record of who changed what. The audit trail keeps at least 90 days.", false, 90),
        new(Messages, "Customer messages", "WhatsApp messages already sent, delivered, read or failed. This is customer history and billing evidence - the clean-up keeps at least 180 days.", true, 180),
    };

    /// <summary>What a fresh install selects: every expendable table, none of the business data.</summary>
    public static readonly IReadOnlyList<string> DefaultSelection = All.Where(t => !t.IsBusinessData).Select(t => t.Key).ToList();

    public static bool IsKnown(string key) => All.Any(t => t.Key == key);
}
