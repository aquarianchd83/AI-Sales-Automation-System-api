using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One tenant's bill for one calendar month - the per-invoice ledger <see cref="Payment"/> and
/// <see cref="Subscription"/> explicitly do not keep (see their own doc comments). Unlike those two,
/// this exists purely for the Platform Admin Console's Invoices screen: nothing else in the system
/// reads it, and no payment gateway writes to it - <see cref="Status"/> only ever changes through a
/// PlatformSuperAdmin's manual "Mark as paid" action (see PlatformInvoiceService.MarkPaidAsync).
///
/// Rows are generated lazily, one per (tenant, calendar UTC month), by
/// PlatformInvoiceService.EnsureInvoicesUpToDateAsync - including the current, still-open month, so a
/// tenant shows up here as soon as it has a plan rather than only once its first month has closed. A
/// CLOSED month's row, once created, is never recomputed: like <see cref="Payment"/>, everything here is
/// snapshotted (including <see cref="CurrencyCode"/>/<see cref="CurrencySymbol"/>, from the tenant's
/// country as of that moment) so a later plan/rate/country change never rewrites history. The current
/// month's row is the one exception - its amounts are refreshed from scratch every time the Invoices
/// screen is opened, so it reads as "month to date" until the month closes, at which point whatever it
/// last showed is what it freezes at (see EnsureInvoicesUpToDateAsync's own doc comment for the gap
/// that leaves if nobody opens the screen near a month boundary).
///
/// <see cref="SubscriptionAmountUsd"/> is an approximation worth calling out: it is the tenant's
/// CURRENT plan price applied flat to the whole month, not whatever plan (or proration) actually
/// applied while that month was live - this system has no per-period subscription charge record to
/// read that back from (see <see cref="Payment"/>'s own doc comment). The other three line items -
/// <see cref="LeadDiscoveryAmountUsd"/>, <see cref="WhatsAppAmountUsd"/>, <see cref="AiConversationAmountUsd"/>
/// - are the same estimates the Usage &amp; Quotas screen shows, just totalled over this fixed,
/// closed period instead of "this month so far".
/// </summary>
public class Invoice : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Start of the billed UTC calendar month, inclusive.</summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>Start of the following UTC calendar month, exclusive - i.e. the period is
    /// [PeriodStartUtc, PeriodEndUtc).</summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>The plan this invoice's subscription line item was priced against, snapshotted at
    /// generation time - null is not expected in practice (invoices are only generated for tenants
    /// that have chosen a plan) but kept nullable rather than assumed, the same caution
    /// PlatformSubscriptionListItemDto.PlanName already takes.</summary>
    public string? PlanName { get; set; }

    public decimal SubscriptionAmountUsd { get; set; }

    /// <summary>Messages sent this period against the plan's monthly allowance - a current-tenant
    /// snapshot the same way SubscriptionAmountUsd is: PlatformUsageService's own message/user counts
    /// are likewise "right now," not reconstructed for a past period.</summary>
    public int MessagesSentCount { get; set; }

    /// <summary>Plan.MaxMessagesPerMonth at generation time - null means the tenant had no resolvable
    /// plan, same "no quota applies" meaning as PlatformTenantUsageDto.MessageLimit.</summary>
    public int? MessageLimit { get; set; }

    public int UserCount { get; set; }

    public int? UserLimit { get; set; }

    public decimal LeadDiscoveryAmountUsd { get; set; }

    public int LeadDiscoveryRunsCount { get; set; }

    public int LeadDiscoveryLeadsCount { get; set; }

    public decimal WhatsAppAmountUsd { get; set; }

    /// <summary>Template sends Meta actually charges for - see WhatsAppSpend.BillableMessages's own
    /// doc comment for why this isn't every message sent.</summary>
    public int WhatsAppBillableMessagesCount { get; set; }

    public decimal AiConversationAmountUsd { get; set; }

    public int AiInteractionsCount { get; set; }

    public decimal TotalAmountUsd { get; set; }

    /// <summary>The tenant's own currency as of generation time (RegionalPricingCatalog.Resolve on
    /// their CountryCode) - stamped once, like Payment.CurrencyCode, so a later country change never
    /// rewrites a past invoice.</summary>
    public string CurrencyCode { get; set; } = string.Empty;

    public string CurrencySymbol { get; set; } = string.Empty;

    public decimal SubscriptionAmountLocal { get; set; }

    public decimal LeadDiscoveryAmountLocal { get; set; }

    public decimal WhatsAppAmountLocal { get; set; }

    public decimal AiConversationAmountLocal { get; set; }

    public decimal TotalAmountLocal { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Due;

    /// <summary>Set when a PlatformSuperAdmin marks this invoice paid - null while Due.</summary>
    public DateTime? PaidAtUtc { get; set; }
}
