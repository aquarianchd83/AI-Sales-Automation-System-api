using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

/// <summary>
/// One time the tenant has asked lead discovery to run by itself (up to five per tenant): a date and a time picked in the tenant's time zone, once or
/// every day at that time. Replaces the single cron of the lead-discovery job. A person's Run now is not a slot and never touches one.
/// </summary>
public class LeadDiscoveryScheduleSlot : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The next time this slot starts a run (UTC). For a slot that has run once and is done, the time it ran.</summary>
    public DateTime NextRunAtUtc { get; set; }

    /// <summary>False: runs once at <see cref="NextRunAtUtc"/> and is then done. True: runs every day at the same local time.</summary>
    public bool RepeatDaily { get; set; }

    /// <summary>A once-only slot that has run. It stays listed until removed and no longer counts toward the five.</summary>
    public bool IsDone { get; set; }

    public DateTime? LastRunAtUtc { get; set; }
}
