using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.MessageTemplates;

public interface IMessageTemplateService
{
    Task<PagedResult<MessageTemplateDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<MessageTemplateDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>New templates start <c>Pending</c>, same as a real Meta submission - see <see cref="ReviewAsync"/>.</summary>
    Task<MessageTemplateDto> CreateAsync(CreateMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task<MessageTemplateDto> UpdateAsync(Guid id, UpdateMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task<MessageTemplateDto> ReviewAsync(Guid id, ReviewMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
