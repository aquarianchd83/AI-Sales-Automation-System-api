using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Setup;

/// <summary>
/// One immutable-once-published revision of what a <see cref="Plan"/> needs to know before an application
/// on it can run ("Lead Generation Plan v2"). Global, like <see cref="Plan"/> itself: owned by the platform,
/// edited by a PlatformSuperAdmin through the Setup Plans screen, never through a tenant-facing endpoint.
///
/// The set of questions lives in <see cref="PlanRequirement"/> rows, so a new plan - or a new question on an
/// existing one - is data, not a release.
/// </summary>
public class PlanSetupVersion : BaseEntity
{
    public Guid PlanId { get; set; }

    /// <summary>1, 2, 3 ... per plan. Never reused, never renumbered.</summary>
    public int VersionNumber { get; set; }

    public SetupVersionStatus Status { get; set; } = SetupVersionStatus.Draft;

    /// <summary>What changed in this version - shown to admins, and to a tenant offered the migration.</summary>
    public string? ReleaseNotes { get; set; }

    /// <summary>How many days a completed setup stays valid before it has to be re-confirmed. Null = never expires.</summary>
    public int? ValidityDays { get; set; }

    public DateTime? PublishedAt { get; set; }

    public Guid? PublishedBy { get; set; }
}
