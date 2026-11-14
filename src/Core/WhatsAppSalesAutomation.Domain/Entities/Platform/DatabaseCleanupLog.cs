using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// One table the automatic database clean-up erased rows from, in one run. Rows from the same run share <see cref="RunId"/> and the before/after
/// figures. Platform-owned, not tenant data. Only tables that actually lost rows are recorded.
/// </summary>
public class DatabaseCleanupLog : BaseEntity
{
    public Guid RunId { get; set; }

    /// <summary>The key from <c>CleanableTableCatalog</c>, e.g. "WebhookEvents".</summary>
    public string TableKey { get; set; } = string.Empty;

    public int RowsDeleted { get; set; }

    /// <summary>Rows newer than this many days were kept.</summary>
    public int KeepDays { get; set; }

    public double PercentUsedBefore { get; set; }

    public double UsedMbBefore { get; set; }

    public double UsedMbAfter { get; set; }
}
