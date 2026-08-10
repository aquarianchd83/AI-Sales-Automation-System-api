using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Customers;

public interface ICustomerService
{
    Task<PagedResult<CustomerDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CustomerDto> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Soft-delete (IsDeleted = true) - preserves history for campaigns/messages built on top in later phases.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CustomerDto> AddTagsAsync(Guid id, AddCustomerTagsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the customer opted-out. Note: actually halting in-flight campaign follow-ups is wired up
    /// in Phase 3 once CampaignCustomers exists - this call only updates the customer record itself.
    /// </summary>
    Task<CustomerDto> OptOutAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CustomerImportResultDto> ImportAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
