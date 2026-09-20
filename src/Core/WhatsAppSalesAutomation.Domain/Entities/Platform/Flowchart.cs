using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A flow diagram authored by a PlatformSuperAdmin in the Content Management System screen - e.g. an
/// onboarding walkthrough or a "how a conversation is handled" diagram. Platform-wide and deliberately
/// NOT <see cref="ITenantOwned"/>: it is authored once by the platform operator and the same content is
/// available everywhere, there is no per-tenant variant.
///
/// <see cref="DiagramJson"/> holds the diagram's nodes/edges exactly as the admin builder UI produced
/// them (an opaque JSON blob to the API/domain - only the client renders it), so the shape of a node or
/// edge can change without a migration.
/// </summary>
public class Flowchart : BaseEntity
{
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Category { get; set; }

    public string DiagramJson { get; set; } = "{}";

    public ContentStatus Status { get; set; } = ContentStatus.Draft;

    /// <summary>Lower sorts first wherever published flowcharts are listed.</summary>
    public int DisplayOrder { get; set; }

    public Guid CreatedByUserId { get; set; }
}
