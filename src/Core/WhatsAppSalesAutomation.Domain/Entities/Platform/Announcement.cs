using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// A platform-wide banner/notice broadcast by a PlatformSuperAdmin to every tenant admin (maintenance
/// windows, new features). Platform-global, not <see cref="ITenantOwned"/> - every tenant sees the
/// same set of active announcements, there is no per-tenant targeting.
/// </summary>
public class Announcement : BaseEntity
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public AnnouncementSeverity Severity { get; set; } = AnnouncementSeverity.Info;

    /// <summary>Whether the announcement is currently shown. A PlatformSuperAdmin can turn one off
    /// early without deleting its history.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional window during which the announcement is shown even while <see cref="IsActive"/>
    /// is true - null means "no bound", so leaving both null shows it indefinitely once active.</summary>
    public DateTime? StartsAtUtc { get; set; }

    public DateTime? EndsAtUtc { get; set; }

    public Guid CreatedByUserId { get; set; }
}

public enum AnnouncementSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
