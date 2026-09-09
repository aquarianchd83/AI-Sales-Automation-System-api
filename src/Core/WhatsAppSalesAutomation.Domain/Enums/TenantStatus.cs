namespace WhatsAppSalesAutomation.Domain.Enums;

public enum TenantStatus
{
    Trial = 0,
    Active = 1,
    Suspended = 2,
    Cancelled = 3,

    /// <summary>Operator-initiated terminal state (Platform Admin Console "delete" action), distinct
    /// from <see cref="Cancelled"/> which is customer-initiated. Never a physical delete - tenant-owned
    /// data is retained, just fully locked out. Reversible only by a PlatformSuperAdmin support action.</summary>
    Deleted = 4
}
