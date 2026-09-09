namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Platform Admin Console's Announcements screen (spec item #11) - a platform-wide banner broadcast
/// to every tenant admin. <see cref="GetActiveAsync"/> is also the tenant-facing read side (any
/// authenticated user, any tenant), everything else is PlatformSuperAdmin-only.
/// </summary>
public interface IAnnouncementService
{
    /// <summary>Every announcement ever created, newest first - the management view.</summary>
    Task<IReadOnlyList<AnnouncementDto>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Announcements a tenant admin should currently see: <c>IsActive</c> and within
    /// [StartsAtUtc, EndsAtUtc] (a null bound is unbounded on that side).</summary>
    Task<IReadOnlyList<AnnouncementDto>> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<AnnouncementDto> CreateAsync(CreateAnnouncementRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<AnnouncementDto> UpdateAsync(Guid id, UpdateAnnouncementRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}
