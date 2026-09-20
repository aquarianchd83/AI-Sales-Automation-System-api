using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A question/answer entry authored by a PlatformSuperAdmin in the Content Management System screen.
/// Platform-wide and deliberately NOT <see cref="ITenantOwned"/> - the same FAQ is shown to every
/// tenant/end user, there is no per-tenant variant.
/// </summary>
public class FaqEntry : BaseEntity
{
    public string Question { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    public string? Category { get; set; }

    public ContentStatus Status { get; set; } = ContentStatus.Draft;

    /// <summary>Lower sorts first wherever published FAQ entries are listed.</summary>
    public int DisplayOrder { get; set; }

    public Guid CreatedByUserId { get; set; }
}
