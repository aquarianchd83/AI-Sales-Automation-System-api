using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform Admin Console's Invoices screen - monthly, per-tenant bills covering the four
/// things a tenant is charged for: subscription fee, lead discovery, WhatsApp messaging and AI
/// conversation usage. See <see cref="Domain.Entities.Billing.Invoice"/> for the generation and
/// snapshot rules.</summary>
public interface IPlatformInvoiceService
{
    Task<PagedResult<PlatformInvoiceListItemDto>> GetPagedAsync(PlatformInvoiceQuery query, CancellationToken cancellationToken = default);

    Task<PlatformInvoiceDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Marks the invoice paid - idempotent, a no-op (returning the invoice unchanged) if it
    /// is already Paid, the same "no-op rather than error" treatment DeactivatePlanAsync gives an
    /// already-retired plan. Throws <see cref="Common.Exceptions.ConflictException"/> for an Upcoming
    /// invoice - the current period is still accruing, so there's nothing final to mark paid yet.</summary>
    Task<PlatformInvoiceDetailDto> MarkPaidAsync(Guid id, CancellationToken cancellationToken = default);
}
