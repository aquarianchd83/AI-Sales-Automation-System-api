using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One row of the Invoices screen - one tenant's bill for one closed calendar month. See
/// <see cref="Domain.Entities.Billing.Invoice"/> for what is and isn't exact about these figures.
/// Money is quoted in that tenant's own currency, same "not comparable across rows" caveat as
/// PlatformTenantUsageDto - the *Usd figures are what to total or compare.</summary>
public record PlatformInvoiceListItemDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    string? PlanName,
    InvoiceStatus Status,
    DateTime? PaidAtUtc,
    decimal TotalAmountUsd,
    decimal TotalAmountLocal,
    string CurrencyCode,
    string CurrencySymbol);

/// <summary>The Invoices screen's detail view - the same header as <see cref="PlatformInvoiceListItemDto"/>
/// plus each of the four line items broken out, alongside the usage count each was charged for
/// (MessagesSentCount/MessageLimit and UserCount/UserLimit are the subscription line's plan quota; the
/// rest are the pay-per-use counts each amount was priced from - see Invoice's own doc comment for how
/// current a UserCount/MessagesSentCount snapshot is for a past period).</summary>
public record PlatformInvoiceDetailDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    string? PlanName,
    InvoiceStatus Status,
    DateTime? PaidAtUtc,
    decimal SubscriptionAmountUsd,
    decimal SubscriptionAmountLocal,
    int MessagesSentCount,
    int? MessageLimit,
    int UserCount,
    int? UserLimit,
    decimal LeadDiscoveryAmountUsd,
    decimal LeadDiscoveryAmountLocal,
    int LeadDiscoveryRunsCount,
    int LeadDiscoveryLeadsCount,
    decimal WhatsAppAmountUsd,
    decimal WhatsAppAmountLocal,
    int WhatsAppBillableMessagesCount,
    decimal AiConversationAmountUsd,
    decimal AiConversationAmountLocal,
    int AiInteractionsCount,
    decimal TotalAmountUsd,
    decimal TotalAmountLocal,
    string CurrencyCode,
    string CurrencySymbol);

public record PlatformInvoiceQuery : PagedRequest
{
    public InvoiceStatus? Status { get; init; }

    public Guid? TenantId { get; init; }
}
