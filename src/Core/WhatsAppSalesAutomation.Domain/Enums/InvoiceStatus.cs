namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Whether and how a tenant has settled one month's <see cref="Entities.Billing.Invoice"/>.
/// The current, still-open month's invoice is always Upcoming - it is still accruing, so there is
/// nothing final to collect yet. Once the month closes, PlatformInvoiceService.EnsureInvoicesUpToDateAsync
/// transitions it to Due exactly once; from there it only ever moves to Paid through a
/// PlatformSuperAdmin's explicit "Mark as paid" action - there is no payment gateway wired in to flip
/// it automatically (Payment.Provider is still "Simulated"), so this is a manual bookkeeping step, not
/// a reconciled charge.</summary>
public enum InvoiceStatus
{
    Due = 0,
    Paid = 1,
    Upcoming = 2
}
