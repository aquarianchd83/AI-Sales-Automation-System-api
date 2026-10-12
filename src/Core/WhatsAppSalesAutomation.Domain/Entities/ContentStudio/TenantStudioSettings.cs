using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.ContentStudio;

/// <summary>
/// A tenant's own AI Content Studio choices. At most one row per tenant, created the first time it is saved; a tenant
/// with no row has the defaults (<see cref="StudioApprovalPolicy.AdminOnly"/>).
/// </summary>
public class TenantStudioSettings : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Who may approve content for publishing. Enforced on the server, not only hidden in the UI.</summary>
    public StudioApprovalPolicy ApprovalPolicy { get; set; } = StudioApprovalPolicy.AdminOnly;
}
