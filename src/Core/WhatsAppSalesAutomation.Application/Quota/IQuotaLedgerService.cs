using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>
/// The only way units move. Every method writes the grant change, the ledger entry and the wallet in
/// one save, so a balance can never disagree with its ledger, and every method is safe to retry.
/// Explicit tenant ids throughout (no ambient tenant): callers are background jobs and platform-admin
/// screens as well as tenant requests.
/// </summary>
public interface IQuotaLedgerService
{
    /// <summary>Usable balance per quota type (expired grants excluded), with the live grants behind it.
    /// Always returns all three types, zero when the tenant has never held any.</summary>
    Task<IReadOnlyList<QuotaBalanceDto>> GetBalancesAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Grants a period's included quota for every quota type the plan defines, expiring at
    /// <paramref name="periodEndUtc"/>. Idempotent per (tenant, period start, type).</summary>
    Task AllocatePlanQuotaAsync(Guid tenantId, Guid planId, DateTime periodStartUtc, DateTime periodEndUtc, Guid? paymentId, CancellationToken cancellationToken = default);

    /// <summary>Grants a purchased pack, valid for <see cref="QuotaLedgerService.CreditValidityMonths"/> months.
    /// Idempotent per payment.</summary>
    Task GrantCreditsAsync(Guid tenantId, CreditPack pack, Guid paymentId, DateTime purchasedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Gives a new tenant its free trial allocation, expiring with the trial. Idempotent per
    /// (tenant, quota type) - calling it again never grants twice.</summary>
    Task GrantTrialQuotaAsync(Guid tenantId, IReadOnlyDictionary<QuotaType, decimal> units, DateTime expiresAtUtc, CancellationToken cancellationToken = default);

    Task<ConsumeResult> ConsumeAsync(ConsumeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns everything a earlier Consume call took (a send that failed before delivery).
    /// Idempotent; units go back to the grants they came from unless those have since expired.</summary>
    Task<bool> ReverseConsumptionAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default);

    /// <summary>A manual correction by a platform admin. A positive delta creates a grant valid for
    /// <paramref name="validForDays"/>; a negative one draws down existing grants.</summary>
    Task AdjustAsync(Guid tenantId, QuotaType quotaType, decimal unitsDelta, string reason, Guid actorUserId, int validForDays = 365, CancellationToken cancellationToken = default);

    /// <summary>Removes up to <paramref name="units"/> of the purchase's own grant (a refund). Returns
    /// what was actually removed - never more than is still unspent.</summary>
    Task<decimal> ClawBackCreditsAsync(Guid tenantId, Guid paymentId, decimal units, string reason, Guid? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>What one payment's grants started with and still have - the basis of refund eligibility.</summary>
    Task<IReadOnlyList<PaymentGrantUsage>> GetPaymentGrantUsageAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>Takes every still-unspent unit of one payment's grants out of the wallet while a refund request
    /// waits, so they can't be spent in the meantime. Idempotent per request. Returns what was held.</summary>
    Task<decimal> HoldForRefundAsync(Guid tenantId, Guid paymentId, Guid refundRequestId, CancellationToken cancellationToken = default);

    /// <summary>Gives held units back: all of them when <paramref name="keepFraction"/> is 0 (rejected, cancelled,
    /// expired), or the unapproved share on a partial approval. Idempotent per request. Returns units released.</summary>
    Task<decimal> ReleaseRefundHoldAsync(Guid tenantId, Guid refundRequestId, decimal keepFraction, CancellationToken cancellationToken = default);

    /// <summary>Writes off whatever is left of the tenant's included plan quota (never purchased credits).
    /// Used when a plan change starts a new paid period, so the old period's quota can't be stacked on the
    /// new one. Returns the units forfeited.</summary>
    Task<decimal> ForfeitPlanAllocationsAsync(Guid tenantId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Writes off every grant past its expiry, across all tenants. Returns grants processed.</summary>
    Task<int> ExpireDueAsync(CancellationToken cancellationToken = default);

    Task<PagedResult<QuotaLedgerEntryDto>> GetLedgerAsync(Guid tenantId, QuotaLedgerQuery query, CancellationToken cancellationToken = default);
}
