using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>Stand-ins for the parts of the single-wallet module a test does not exercise, so a gate or a service can be built over a plain ledger.</summary>
internal static class CreditTestDoubles
{
    /// <summary>A gate over <paramref name="ledger"/> whose "what can this tenant still afford" is simply its credit balance (every feature in the plan, no cap, one credit per unit).</summary>
    public static QuotaGate Gate(IQuotaLedgerService ledger) =>
        new(ledger, Snapshot.Of(new WhatsAppPricingOptions()), new NoPlanChanges(), new BalanceWallet(ledger));

    private sealed class NoPlanChanges : ICreditAllocationService
    {
        public Task AssignPlanAsync(Guid tenantId, Guid planId, Guid? paymentId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AssignTrialPlanAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> AllocateMonthlyAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class BalanceWallet : ICreditWalletService
    {
        private readonly IQuotaLedgerService _ledger;

        public BalanceWallet(IQuotaLedgerService ledger) => _ledger = ledger;

        public async Task<decimal> GetAffordableUnitsAsync(Guid tenantId, CreditScenario scenario, CancellationToken cancellationToken = default) =>
            (await _ledger.GetBalancesAsync(tenantId, cancellationToken)).Single(b => b.QuotaType == QuotaType.Credits).Balance;

        public Task<EntitlementsDto> GetEntitlementsAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WalletSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WalletSummaryDto> SetCapAsync(Guid tenantId, SetFeatureCapRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PagedResult<QuotaLedgerEntryDto>> GetFeatureLedgerAsync(Guid tenantId, PlatformFeature feature, PagedRequest paging, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<decimal?> GetCapRemainingAsync(Guid tenantId, PlatformFeature feature, CancellationToken cancellationToken = default) => Task.FromResult<decimal?>(null);
    }
}
