using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>The Credit Payers screen's service: who pays for each scenario, the platform wallet behind it, and the record of who paid.
/// Explicit tenant ids throughout - this reads across tenants.</summary>
public interface ICreditPayerService
{
    Task<IReadOnlyList<CreditScenarioDto>> GetScenariosAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes who pays for a scenario from now on. Past records keep the payer they were charged to.</summary>
    Task<CreditScenarioDto> SetPayerAsync(Domain.Enums.CreditScenario scenario, UpdateCreditPayerRequest request, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlatformCreditWalletDto>> GetWalletsAsync(CancellationToken cancellationToken = default);

    /// <summary>A top-up (positive) or a correction (negative, never below zero) of the platform wallet. A reason is required.</summary>
    Task<IReadOnlyList<PlatformCreditWalletDto>> AdjustWalletAsync(AdjustPlatformWalletRequest request, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<PagedResult<PlatformCreditEntryDto>> GetWalletLedgerAsync(PlatformCreditEntryQuery query, CancellationToken cancellationToken = default);

    Task<PagedResult<CreditUsageDto>> GetUsageAsync(CreditUsageQuery query, CancellationToken cancellationToken = default);

    /// <summary>Gives a tenant back what a spend charged it - the goodwill option, for when the tenant paid and should not have had to (for
    /// example the platform wallet ran dry and it fell back to them). Adds a fresh grant to the tenant, so it works even after the original
    /// credits expired. Only a charged, tenant-paid spend can be redeemed, and only once.</summary>
    Task<CreditUsageDto> RedeemToTenantAsync(Guid usageId, string note, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>The tenant's own credit usage, newest first, including the actions the platform covered.</summary>
    Task<PagedResult<TenantCreditUsageDto>> GetTenantUsageAsync(Guid tenantId, TenantCreditUsageQuery query, CancellationToken cancellationToken = default);
}
