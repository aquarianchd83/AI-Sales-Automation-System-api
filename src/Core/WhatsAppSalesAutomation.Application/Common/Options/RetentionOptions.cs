namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "Retention" config section (also editable in the Settings screen). How long the platform keeps what it only needs for a
/// while. Every value is in days; <c>0</c> keeps that kind of data forever, and anything between 1 and 6 counts as 7, so a typo cannot
/// wipe a week's records overnight.
///
/// Deliberately NOT here: conversations and messages. Those are the customer relationship and the billing evidence, and erasing a
/// person's data on request is a different, per-customer action rather than an age cut-off.
/// </summary>
public class RetentionOptions
{
    public const int MinimumDays = 7;

    /// <summary>Raw webhook payloads (customer message text and all) once they have been processed or have failed. Unprocessed ones are never touched.</summary>
    public int WebhookEventDays { get; set; } = 90;

    /// <summary>Refresh-token rows after they expired or were revoked. A token this old can no longer be used, so nothing depends on the row.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Notifications someone has already read. An unread one stays until it is.</summary>
    public int NotificationDays { get; set; } = 180;

    /// <summary>AI turns: what the model saw and proposed for each inbound message. The AI performance reports look back less far than this.</summary>
    public int AiInteractionDays { get; set; } = 365;

    /// <summary>Audit trail entries. The trail is append-only for everyone; this age cut-off is the one way rows leave it.</summary>
    public int AuditLogDays { get; set; } = 730;

    /// <summary>Rows removed per statement, so one pass never holds a long lock on a big table.</summary>
    public int BatchSize { get; set; } = 2000;

    /// <summary>The age cut-off for a setting, or null when it is off (0 or negative).</summary>
    public static int? Effective(int days) => days <= 0 ? null : Math.Max(days, MinimumDays);
}
