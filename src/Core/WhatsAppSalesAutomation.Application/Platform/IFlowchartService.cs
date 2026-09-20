namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// Content Management System - the Flowcharts screen. <see cref="GetPublishedAsync"/> is the read side
/// open to any authenticated user (whatever consumes the published diagrams); every other action is
/// PlatformSuperAdmin-only.
/// </summary>
public interface IFlowchartService
{
    /// <summary>Every flowchart regardless of status, ordered for the management view.</summary>
    Task<IReadOnlyList<FlowchartDto>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Published flowcharts only, in display order.</summary>
    Task<IReadOnlyList<FlowchartDto>> GetPublishedAsync(CancellationToken cancellationToken = default);

    Task<FlowchartDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FlowchartDto> CreateAsync(CreateFlowchartRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task<FlowchartDto> UpdateAsync(Guid id, UpdateFlowchartRequest request, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}
