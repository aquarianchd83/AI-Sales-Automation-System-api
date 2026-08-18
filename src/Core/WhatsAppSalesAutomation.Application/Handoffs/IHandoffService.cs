using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Handoffs;

public interface IHandoffService
{
    /// <summary><paramref name="status"/> filters to one HandoffStatus value when given (e.g. "Pending").</summary>
    Task<PagedResult<HandoffDto>> GetPagedAsync(PagedRequest request, string? status = null, CancellationToken cancellationToken = default);

    Task<HandoffDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Pending -> Assigned, to this agent. Re-claiming an already-Assigned handoff reassigns
    /// it rather than failing - the last agent to claim it owns it, no lock contention modelling.</summary>
    Task<HandoffDto> ClaimAsync(Guid id, Guid agentId, CancellationToken cancellationToken = default);

    Task<HandoffDto> ResolveAsync(Guid id, ResolveHandoffRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversation's current non-Resolved handoff if one exists, otherwise a freshly created
    /// Pending one. Used by InboundWebhookProcessor so a chatty customer's follow-up messages do not
    /// spawn a new queue entry for every message while one is already open.
    /// </summary>
    Task<HandoffDto> GetOrCreateOpenHandoffAsync(Guid conversationId, string triggerReason, string? notes, CancellationToken cancellationToken = default);
}
