namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>One template category's share of a tenant's WhatsApp spend.</summary>
public record WhatsAppCategorySpend(string Category, int Messages, decimal RatePerMessageUsd, decimal EstimatedCostUsd);

/// <summary>
/// What a tenant's WhatsApp sending cost over a period. <paramref name="MessagesSent"/> counts every message
/// row in the period - inbound and outbound, the same population the plan's monthly message allowance is
/// measured against - while <paramref name="BillableMessages"/> counts only the template sends Meta charges
/// for. The two differ on purpose, so a screen can show "1,200 messages, 300 of them billable" rather than
/// implying every message costs money.
/// </summary>
public record WhatsAppSpend(
    int MessagesSent,
    int BillableMessages,
    int FreeMessages,
    decimal EstimatedCostUsd,
    IReadOnlyList<WhatsAppCategorySpend> ByCategory)
{
    public static readonly WhatsAppSpend Empty = new(0, 0, 0, 0m, Array.Empty<WhatsAppCategorySpend>());
}

/// <summary>
/// Prices the WhatsApp messages a tenant has sent, from the hand-maintained rates in
/// Common.Options.WhatsAppPricingOptions - an estimate, never an invoice; see that class for the caveats.
///
/// Costs are computed on read rather than stamped onto each Message when it is sent, unlike a lead discovery
/// run (which stores the cost it was priced at). The trade-off: no schema change and no send-path change, but
/// correcting a rate silently restates past figures. That is the right way round for a dashboard estimate,
/// and the wrong way round for anything anyone invoices from - if these numbers ever become billing, stamp
/// the rate onto the message at send time first.
/// </summary>
public interface IWhatsAppSpendService
{
    /// <summary>One tenant's spend from <paramref name="fromUtc"/> up to <paramref name="toUtc"/>
    /// (exclusive), or through now when <paramref name="toUtc"/> is omitted - the open-ended window
    /// every caller before PlatformInvoiceService needed ("since the start of this month, to now").</summary>
    Task<WhatsAppSpend> GetForTenantAsync(Guid tenantId, DateTime fromUtc, DateTime? toUtc = null, CancellationToken cancellationToken = default);

    /// <summary>The same figures for many tenants at once, for the Platform Admin Console's usage and
    /// invoices screens. A tenant that sent nothing in the period is absent from the result, not a
    /// zero row.</summary>
    Task<IReadOnlyDictionary<Guid, WhatsAppSpend>> GetForTenantsAsync(
        IReadOnlyCollection<Guid> tenantIds, DateTime fromUtc, DateTime? toUtc = null, CancellationToken cancellationToken = default);
}
