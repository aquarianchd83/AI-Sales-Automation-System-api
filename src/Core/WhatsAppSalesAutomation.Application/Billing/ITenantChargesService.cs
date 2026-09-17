namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>
/// What this calendar month has cost one tenant to run: WhatsApp sending plus lead discovery, the two things
/// that spend money per use. The tenant's plan fee is not part of it - that is a subscription, shown on the
/// Billing screen, not usage.
///
/// Every figure is an estimate priced from hand-maintained rate tables (see WhatsAppPricingOptions and
/// LeadDiscoveryPricingOptions); the platform's own Meta and provider invoices are the authority. Whatever
/// renders this must say so.
/// </summary>
public interface ITenantChargesService
{
    /// <summary>The calling tenant's charges since the start of the current UTC calendar month - the same
    /// "resets on the 1st" period the message allowance uses, so the two screens agree.</summary>
    Task<TenantChargesDto> GetCurrentMonthAsync(CancellationToken cancellationToken = default);
}
