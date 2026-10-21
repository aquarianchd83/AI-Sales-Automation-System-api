using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>
/// See <see cref="ICreditRouter"/>. The tenant side is the existing <see cref="QuotaLedgerService"/>, untouched; this only decides, per
/// scenario, whether the spend goes there or to the platform wallet, and writes the <see cref="CreditUsage"/> record of who paid. Recording
/// is best effort: the credits have already moved by then, so a failure to write the record is logged and never fails the spend.
/// </summary>
public class CreditRouter : ICreditRouter
{
    private const int MaxAttempts = 5;

    private readonly IApplicationDbContext _context;
    private readonly QuotaLedgerService _tenantLedger;
    private readonly IDateTimeProvider _dateTime;
    private readonly ILogger<CreditRouter> _logger;

    public CreditRouter(IApplicationDbContext context, QuotaLedgerService tenantLedger, IDateTimeProvider dateTime, ILogger<CreditRouter> logger)
    {
        _context = context;
        _tenantLedger = tenantLedger;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task<ConsumeResult> ConsumeAsync(ConsumeRequest request, CancellationToken cancellationToken = default)
    {
        if (CreditScenarioCatalog.Classify(request) is not { } scenario)
            return await _tenantLedger.ConsumeAsync(request, cancellationToken);

        var existing = await FindUsageAsync(request.TenantId, request.OperationKey, cancellationToken);
        if (existing is not null)
            return await AlreadySpentAsync(existing, request, cancellationToken);

        var setting = await GetSettingAsync(scenario, cancellationToken);
        var description = request.Note ?? CreditScenarioCatalog.For(scenario).Title;
        var fellBack = false;

        if (setting.Payer == CreditPayer.Platform)
        {
            var spent = await TrySpendFromPlatformAsync(request, scenario, description, cancellationToken);
            if (spent is not null)
                return spent;

            // The platform wallet cannot cover it. The customer still gets answered; the tenant pays, and the record says why.
            fellBack = true;
        }

        var result = await _tenantLedger.ConsumeAsync(request, cancellationToken);
        if (result.Consumed > 0 && !result.AlreadyApplied)
        {
            await RecordAsync(new CreditUsage
            {
                TenantId = request.TenantId,
                Scenario = scenario,
                QuotaType = request.QuotaType,
                Units = result.Consumed,
                ConfiguredPayer = setting.Payer,
                ChargedTo = CreditPayer.Tenant,
                FellBackToTenant = fellBack,
                Status = CreditUsageStatus.Charged,
                OperationKey = request.OperationKey,
                ReferenceType = request.ReferenceType,
                ReferenceId = request.ReferenceId,
                Description = Truncate(description, 300),
                OccurredAtUtc = _dateTime.UtcNow
            }, cancellationToken);
        }

        return result;
    }

    public async Task<bool> ReverseAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default)
    {
        var usage = await FindUsageAsync(tenantId, operationKey, cancellationToken);
        if (usage is null)
            return await _tenantLedger.ReverseConsumptionAsync(tenantId, operationKey, cancellationToken);

        // Already given back (by an earlier reversal, or redeemed by an admin), or never charged: nothing more to return.
        if (usage.Status != CreditUsageStatus.Charged)
            return true;

        if (usage.ChargedTo == CreditPayer.Tenant)
        {
            var reversed = await _tenantLedger.ReverseConsumptionAsync(tenantId, operationKey, cancellationToken);
            await SettleAsync(tenantId, operationKey, CreditUsageStatus.Reversed, null, null, cancellationToken);
            return reversed;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var current = await FindUsageAsync(tenantId, operationKey, cancellationToken);
                if (current is null || current.Status != CreditUsageStatus.Charged)
                    return true;

                var now = _dateTime.UtcNow;
                var (wallet, _) = await GetOrCreateWalletAsync(current.QuotaType, cancellationToken);
                wallet.Balance += current.Units;
                _context.PlatformCreditEntries.Add(new PlatformCreditEntry
                {
                    QuotaType = current.QuotaType,
                    EntryType = PlatformCreditEntryType.Reversal,
                    UnitsDelta = current.Units,
                    BalanceAfter = wallet.Balance,
                    OperationKey = $"rev:{operationKey}",
                    TenantId = tenantId,
                    Note = "Reversal of " + operationKey,
                    OccurredAtUtc = now
                });
                current.Status = CreditUsageStatus.Reversed;
                current.SettledAtUtc = now;
                await _context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                _context.ResetChangeTracker();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    public async Task ChargeActionAsync(Guid tenantId, CreditScenario scenario, string operationKey, string description, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = CreditScenarioCatalog.For(scenario);
            var setting = await GetSettingAsync(scenario, cancellationToken);
            var units = setting.UnitsPerAction ?? info.DefaultUnits;
            if (units <= 0)
                return;

            var result = await ConsumeAsync(
                new ConsumeRequest(tenantId, info.QuotaType, units, operationKey, CreditScenarioCatalog.ProductKnowledgeReference, operationKey, description, Scenario: scenario),
                cancellationToken);

            if (!result.Sufficient)
            {
                // The AI call has already been made and paid for; nobody can cover it, so say so rather than hide it.
                await RecordAsync(new CreditUsage
                {
                    TenantId = tenantId,
                    Scenario = scenario,
                    QuotaType = info.QuotaType,
                    Units = units,
                    ConfiguredPayer = setting.Payer,
                    ChargedTo = setting.Payer,
                    Status = CreditUsageStatus.Unpaid,
                    OperationKey = operationKey,
                    ReferenceType = CreditScenarioCatalog.ProductKnowledgeReference,
                    ReferenceId = operationKey,
                    Description = Truncate(description, 300),
                    OccurredAtUtc = _dateTime.UtcNow
                }, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not charge {Scenario} credits for tenant {TenantId}", scenario, tenantId);
        }
    }

    // ----- platform wallet --------------------------------------------------------------------------------------------------------

    /// <summary>Takes the request's units from the platform wallet. Null when the wallet cannot cover all of it - the caller then falls back to
    /// the tenant. A partial spend is never split across the two accounts, so each record has exactly one payer.</summary>
    private async Task<ConsumeResult?> TrySpendFromPlatformAsync(ConsumeRequest request, CreditScenario scenario, string description, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var existing = await FindUsageAsync(request.TenantId, request.OperationKey, cancellationToken);
                if (existing is not null)
                    return await AlreadySpentAsync(existing, request, cancellationToken);

                var (wallet, created) = await GetOrCreateWalletAsync(request.QuotaType, cancellationToken);
                if (wallet.Balance < request.Units)
                {
                    // A wallet row only just created here is dropped again, so the caller's next save does not write a stray empty one.
                    if (created)
                        _context.PlatformCreditWallets.Remove(wallet);
                    return null;
                }

                var now = _dateTime.UtcNow;
                wallet.Balance -= request.Units;
                _context.PlatformCreditEntries.Add(new PlatformCreditEntry
                {
                    QuotaType = request.QuotaType,
                    EntryType = PlatformCreditEntryType.Consumption,
                    UnitsDelta = -request.Units,
                    BalanceAfter = wallet.Balance,
                    OperationKey = request.OperationKey,
                    TenantId = request.TenantId,
                    Note = description,
                    OccurredAtUtc = now
                });
                _context.CreditUsages.Add(new CreditUsage
                {
                    TenantId = request.TenantId,
                    Scenario = scenario,
                    QuotaType = request.QuotaType,
                    Units = request.Units,
                    ConfiguredPayer = CreditPayer.Platform,
                    ChargedTo = CreditPayer.Platform,
                    Status = CreditUsageStatus.Charged,
                    OperationKey = request.OperationKey,
                    ReferenceType = request.ReferenceType,
                    ReferenceId = request.ReferenceId,
                    Description = Truncate(description, 300),
                    OccurredAtUtc = now
                });
                await _context.SaveChangesAsync(cancellationToken);
                return new ConsumeResult(request.Units, request.Units, wallet.Balance, true, AlreadyApplied: false);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                _context.ResetChangeTracker();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task<(PlatformCreditWallet Wallet, bool Created)> GetOrCreateWalletAsync(QuotaType type, CancellationToken cancellationToken)
    {
        var wallet = await _context.PlatformCreditWallets.FirstOrDefaultAsync(w => w.QuotaType == type, cancellationToken);
        if (wallet is not null)
            return (wallet, false);

        wallet = new PlatformCreditWallet { QuotaType = type };
        _context.PlatformCreditWallets.Add(wallet);
        return (wallet, true);
    }

    private async Task<ConsumeResult> AlreadySpentAsync(CreditUsage existing, ConsumeRequest request, CancellationToken cancellationToken)
    {
        // A tenant-paid spend is idempotent in the tenant ledger itself; the platform's is answered from its own record.
        if (existing.ChargedTo == CreditPayer.Tenant)
            return await _tenantLedger.ConsumeAsync(request, cancellationToken);

        var balance = await _context.PlatformCreditWallets.Where(w => w.QuotaType == existing.QuotaType)
            .Select(w => w.Balance).FirstOrDefaultAsync(cancellationToken);
        return new ConsumeResult(request.Units, existing.Units, balance, existing.Units >= request.Units, AlreadyApplied: true);
    }

    // ----- records ----------------------------------------------------------------------------------------------------------------

    private Task<CreditUsage?> FindUsageAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken) =>
        _context.CreditUsages.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.OperationKey == operationKey, cancellationToken);

    private async Task RecordAsync(CreditUsage usage, CancellationToken cancellationToken)
    {
        try
        {
            _context.CreditUsages.Add(usage);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.ResetChangeTracker();
            _logger.LogWarning(ex, "Could not record the credit usage for {OperationKey}", usage.OperationKey);
        }
    }

    private async Task SettleAsync(Guid tenantId, string operationKey, CreditUsageStatus status, Guid? actor, string? note, CancellationToken cancellationToken)
    {
        try
        {
            // Read again: the tenant ledger resets the change tracker when it retries, so an earlier read may no longer be tracked.
            var usage = await FindUsageAsync(tenantId, operationKey, cancellationToken);
            if (usage is null)
                return;

            usage.Status = status;
            usage.SettledAtUtc = _dateTime.UtcNow;
            usage.SettledByUserId = actor;
            usage.SettlementNote = note;
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.ResetChangeTracker();
            _logger.LogWarning(ex, "Could not mark the credit usage {OperationKey} as {Status}", operationKey, status);
        }
    }

    private async Task<(CreditPayer Payer, decimal? UnitsPerAction)> GetSettingAsync(CreditScenario scenario, CancellationToken cancellationToken)
    {
        var row = await _context.CreditScenarioSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Scenario == scenario, cancellationToken);
        return row is null ? (CreditScenarioCatalog.For(scenario).DefaultPayer, null) : (row.Payer, row.UnitsPerAction);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
