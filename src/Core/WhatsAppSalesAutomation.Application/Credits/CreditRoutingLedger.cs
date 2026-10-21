using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>
/// The <see cref="IQuotaLedgerService"/> the rest of the application sees. Every path that spends or returns credits already goes through
/// this interface, so putting the payer decision here means none of them changes: a spend is handed to <see cref="ICreditRouter"/>, which sends
/// it to the tenant's ledger or the platform wallet, and everything else passes straight to the tenant ledger.
/// </summary>
public class CreditRoutingLedger : IQuotaLedgerService
{
    private readonly QuotaLedgerService _inner;
    private readonly ICreditRouter _router;

    public CreditRoutingLedger(QuotaLedgerService inner, ICreditRouter router)
    {
        _inner = inner;
        _router = router;
    }

    public Task<ConsumeResult> ConsumeAsync(ConsumeRequest request, CancellationToken cancellationToken = default) =>
        _router.ConsumeAsync(request, cancellationToken);

    public Task<bool> ReverseConsumptionAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default) =>
        _router.ReverseAsync(tenantId, operationKey, cancellationToken);

    public Task<IReadOnlyList<QuotaBalanceDto>> GetBalancesAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        _inner.GetBalancesAsync(tenantId, cancellationToken);

    public Task AllocatePlanQuotaAsync(Guid tenantId, Guid planId, DateTime periodStartUtc, DateTime periodEndUtc, Guid? paymentId, CancellationToken cancellationToken = default) =>
        _inner.AllocatePlanQuotaAsync(tenantId, planId, periodStartUtc, periodEndUtc, paymentId, cancellationToken);

    public Task GrantCreditsAsync(Guid tenantId, CreditPack pack, Guid paymentId, DateTime purchasedAtUtc, CancellationToken cancellationToken = default) =>
        _inner.GrantCreditsAsync(tenantId, pack, paymentId, purchasedAtUtc, cancellationToken);

    public Task GrantTrialQuotaAsync(Guid tenantId, IReadOnlyDictionary<QuotaType, decimal> units, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
        _inner.GrantTrialQuotaAsync(tenantId, units, expiresAtUtc, cancellationToken);

    public Task AdjustAsync(Guid tenantId, QuotaType quotaType, decimal unitsDelta, string reason, Guid actorUserId, int validForDays = 365, CancellationToken cancellationToken = default) =>
        _inner.AdjustAsync(tenantId, quotaType, unitsDelta, reason, actorUserId, validForDays, cancellationToken);

    public Task<decimal> ClawBackCreditsAsync(Guid tenantId, Guid paymentId, decimal units, string reason, Guid? actorUserId, CancellationToken cancellationToken = default) =>
        _inner.ClawBackCreditsAsync(tenantId, paymentId, units, reason, actorUserId, cancellationToken);

    public Task<IReadOnlyList<PaymentGrantUsage>> GetPaymentGrantUsageAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken = default) =>
        _inner.GetPaymentGrantUsageAsync(tenantId, paymentId, cancellationToken);

    public Task<decimal> HoldForRefundAsync(Guid tenantId, Guid paymentId, Guid refundRequestId, CancellationToken cancellationToken = default) =>
        _inner.HoldForRefundAsync(tenantId, paymentId, refundRequestId, cancellationToken);

    public Task<decimal> ReleaseRefundHoldAsync(Guid tenantId, Guid refundRequestId, decimal keepFraction, CancellationToken cancellationToken = default) =>
        _inner.ReleaseRefundHoldAsync(tenantId, refundRequestId, keepFraction, cancellationToken);

    public Task<decimal> ForfeitPlanAllocationsAsync(Guid tenantId, string reason, CancellationToken cancellationToken = default) =>
        _inner.ForfeitPlanAllocationsAsync(tenantId, reason, cancellationToken);

    public Task<int> ExpireDueAsync(CancellationToken cancellationToken = default) =>
        _inner.ExpireDueAsync(cancellationToken);

    public Task<PagedResult<QuotaLedgerEntryDto>> GetLedgerAsync(Guid tenantId, QuotaLedgerQuery query, CancellationToken cancellationToken = default) =>
        _inner.GetLedgerAsync(tenantId, query, cancellationToken);
}
