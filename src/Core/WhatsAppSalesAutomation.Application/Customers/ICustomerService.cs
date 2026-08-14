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

    /// <summary>
    /// Soft-deletes many customers in one round trip. Partial success: ids that match nothing are
    /// returned in the result rather than aborting the whole batch.
    /// </summary>
    Task<BulkDeleteCustomersResultDto> BulkDeleteAsync(BulkDeleteCustomersRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> AddTagsAsync(Guid id, AddCustomerTagsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches the named tag from this customer. Case-insensitive, matching AddTagsAsync's own
    /// lookup. Does not delete the CustomerTag entity itself - other customers may still reference
    /// it - and is a no-op (not an error) if the customer never had it, so a double-click or a retry
    /// after a dropped response can't fail.
    /// </summary>
    Task<CustomerDto> RemoveTagAsync(Guid id, string tagName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records consent, which is what makes a customer messageable at all once Phase 3 enforces the
    /// send gate. Idempotent: re-opting-in an already opted-in customer preserves the original
    /// timestamp and source, since that first record is the evidence.
    /// </summary>
    Task<CustomerDto> OptInAsync(Guid id, OptInCustomerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the customer opted-out. Note: actually halting in-flight campaign follow-ups is wired up
    /// in Phase 3 once CampaignCustomers exists - this call only updates the customer record itself.
    /// </summary>
    Task<CustomerDto> OptOutAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CustomerImportResultDto> ImportAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
