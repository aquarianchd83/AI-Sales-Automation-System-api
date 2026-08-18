using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Conversations;

public interface IConversationService
{
    /// <summary><paramref name="status"/> filters to one ConversationStatus value when given (e.g. "Open").</summary>
    Task<PagedResult<ConversationDto>> GetPagedAsync(PagedRequest request, string? status = null, CancellationToken cancellationToken = default);

    Task<ConversationDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<ConversationMessageDto>> GetMessagesAsync(Guid conversationId, PagedRequest request, CancellationToken cancellationToken = default);

    Task<ConversationDto> ChangeModeAsync(Guid id, ChangeConversationModeRequest request, CancellationToken cancellationToken = default);

    Task<ConversationDto> AssignAsync(Guid id, AssignConversationRequest request, CancellationToken cancellationToken = default);

    Task<ConversationDto> CloseAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The manual agent reply. Enforces the customer service window for free text (400 outside it,
    /// naming how many hours ago the window closed) and requires an Approved, active template
    /// otherwise - the same rule CampaignService.ValidateSendableAsync applies to campaign steps.
    /// </summary>
    Task<ConversationMessageDto> SendMessageAsync(Guid id, SendConversationMessageRequest request, Guid sentByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer's current non-Closed conversation, or a freshly created one if none exists.
    /// Shared by CampaignSendService (outbound) and InboundWebhookProcessor (inbound) so "what counts
    /// as the active thread" is decided in exactly one place.
    /// </summary>
    Task<Guid> GetOrCreateActiveConversationIdAsync(Guid customerId, CancellationToken cancellationToken = default);
}
