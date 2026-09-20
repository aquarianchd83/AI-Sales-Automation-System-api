namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Content Management System - the FAQ screen. <see cref="GetPublishedAsync"/> is the read side open to
/// any authenticated user; every other action is PlatformSuperAdmin-only.
/// </summary>
public interface IFaqService
{
    /// <summary>Every FAQ entry regardless of status, ordered for the management view.</summary>
    Task<IReadOnlyList<FaqEntryDto>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Published FAQ entries only, in display order.</summary>
    Task<IReadOnlyList<FaqEntryDto>> GetPublishedAsync(CancellationToken cancellationToken = default);

    Task<FaqEntryDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FaqEntryDto> CreateAsync(CreateFaqEntryRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<FaqEntryDto> UpdateAsync(Guid id, UpdateFaqEntryRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}
