using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>See <see cref="ICreditPayerService"/>. Like the other platform services it reads across tenants, so every tenant-owned read here
/// names its tenant explicitly and ignores the ambient filter.</summary>
public class CreditPayerService : ICreditPayerService
{
    private const int SummaryDays = 30;
    private const int MaxAttempts = 5;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IQuotaLedgerService _tenantLedger;

    public CreditPayerService(IApplicationDbContext context, IDateTimeProvider dateTime, QuotaLedgerService tenantLedger)
    {
        _context = context;
        _dateTime = dateTime;
        _tenantLedger = tenantLedger;
    }

    public async Task<IReadOnlyList<CreditScenarioDto>> GetScenariosAsync(CancellationToken cancellationToken = default)
    {
        var since = _dateTime.UtcNow.AddDays(-SummaryDays);

        var settings = await _context.CreditScenarioSettings.AsNoTracking().ToListAsync(cancellationToken);
        var balances = await _context.PlatformCreditWallets.AsNoTracking().ToDictionaryAsync(w => w.QuotaType, w => w.Balance, cancellationToken);

        var usage = (await _context.CreditUsages.AsNoTracking()
                .Where(u => u.OccurredAtUtc >= since && u.Status != CreditUsageStatus.Unpaid)
                .GroupBy(u => u.Scenario)
                .Select(g => new
                {
                    Scenario = g.Key,
                    Actions = g.Count(),
                    Platform = g.Sum(u => u.ChargedTo == CreditPayer.Platform && u.Status == CreditUsageStatus.Charged ? u.Units : 0m),
                    Tenant = g.Sum(u => u.ChargedTo == CreditPayer.Tenant && u.Status == CreditUsageStatus.Charged ? u.Units : 0m),
                    Fallbacks = g.Count(u => u.FellBackToTenant)
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(g => g.Scenario);

        // The provider's own bill for the same period, by AI task, folded into the scenarios the tasks belong to.
        var costByTask = await _context.AiExecutions.AsNoTracking()
            .Where(e => e.CreatedAt >= since && e.Mode == AiExecutionMode.Live && e.Status == AiExecutionStatus.Succeeded && e.EstimatedCostUsd != null)
            .GroupBy(e => e.Task)
            .Select(g => new { Task = g.Key, Cost = g.Sum(e => e.EstimatedCostUsd!.Value) })
            .ToListAsync(cancellationToken);
        var costByScenario = costByTask
            .Select(c => (Scenario: CreditScenarioCatalog.ForAiTask(c.Task), c.Cost))
            .Where(c => c.Scenario is not null)
            .GroupBy(c => c.Scenario!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Cost));

        return CreditScenarioCatalog.All.Select(info =>
        {
            var row = settings.FirstOrDefault(s => s.Scenario == info.Scenario);
            usage.TryGetValue(info.Scenario, out var u);
            return new CreditScenarioDto(
                info.Scenario, info.Title, info.Area, info.Description, info.QuotaType,
                row?.Payer ?? info.DefaultPayer, info.DefaultPayer, info.UnitsConfigurable,
                // What one unit (one action, for product knowledge) costs in credits: the platform's Credit costs setting, or the default.
                info.UnitsConfigurable ? row?.UnitsPerAction ?? info.DefaultUnits : row?.CreditsPerUnit ?? info.DefaultUnits,
                balances.GetValueOrDefault(info.QuotaType),
                u?.Actions ?? 0, u?.Platform ?? 0m, u?.Tenant ?? 0m, u?.Fallbacks ?? 0,
                costByScenario.TryGetValue(info.Scenario, out var cost) ? Math.Round(cost, 4) : null,
                row?.UpdatedAt ?? row?.CreatedAt,
                info.UnitLabel);
        }).ToList();
    }

    public async Task<CreditScenarioDto> SetPayerAsync(CreditScenario scenario, UpdateCreditPayerRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var info = CreditScenarioCatalog.All.FirstOrDefault(i => i.Scenario == scenario)
            ?? throw new NotFoundException("Credit scenario not found.");
        if (!Enum.IsDefined(request.Payer))
            throw Invalid(nameof(request.Payer), "Choose the tenant or the platform.");
        if (request.UnitsPerAction is < 0)
            throw Invalid(nameof(request.UnitsPerAction), "Credits per action cannot be negative.");

        var row = await _context.CreditScenarioSettings.FirstOrDefaultAsync(s => s.Scenario == scenario, cancellationToken);
        if (row is null)
        {
            row = new CreditScenarioSetting { Scenario = scenario };
            _context.CreditScenarioSettings.Add(row);
        }

        row.Payer = request.Payer;
        if (info.UnitsConfigurable)
            row.UnitsPerAction = request.UnitsPerAction ?? row.UnitsPerAction ?? info.DefaultUnits;
        else if (request.UnitsPerAction is { } creditsPerUnit)
            row.CreditsPerUnit = creditsPerUnit;
        row.UpdatedByUserId = actorUserId;
        await _context.SaveChangesAsync(cancellationToken);

        return (await GetScenariosAsync(cancellationToken)).Single(s => s.Scenario == scenario);
    }

    public async Task<IReadOnlyList<PlatformCreditWalletDto>> GetWalletsAsync(CancellationToken cancellationToken = default)
    {
        var balances = await _context.PlatformCreditWallets.AsNoTracking().ToDictionaryAsync(w => w.QuotaType, w => w.Balance, cancellationToken);
        return new[] { QuotaType.Credits }.Select(type => new PlatformCreditWalletDto(type, balances.GetValueOrDefault(type))).ToList();
    }

    public async Task<IReadOnlyList<PlatformCreditWalletDto>> AdjustWalletAsync(AdjustPlatformWalletRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (request.UnitsDelta == 0)
            throw Invalid(nameof(request.UnitsDelta), "An adjustment must change something.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw Invalid(nameof(request.Reason), "An adjustment needs a reason.");
        if (!Enum.IsDefined(request.QuotaType))
            throw Invalid(nameof(request.QuotaType), "Choose a credit type.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var wallet = await _context.PlatformCreditWallets.FirstOrDefaultAsync(w => w.QuotaType == request.QuotaType, cancellationToken);
                if (wallet is null)
                {
                    wallet = new PlatformCreditWallet { QuotaType = request.QuotaType };
                    _context.PlatformCreditWallets.Add(wallet);
                }

                if (wallet.Balance + request.UnitsDelta < 0)
                    throw new ConflictException($"The platform wallet only has {wallet.Balance:#,##0.####} left, so it cannot be reduced by {-request.UnitsDelta:#,##0.####}.");

                wallet.Balance += request.UnitsDelta;
                _context.PlatformCreditEntries.Add(new PlatformCreditEntry
                {
                    QuotaType = request.QuotaType,
                    EntryType = request.UnitsDelta > 0 ? PlatformCreditEntryType.TopUp : PlatformCreditEntryType.AdjustmentDebit,
                    UnitsDelta = request.UnitsDelta,
                    BalanceAfter = wallet.Balance,
                    OperationKey = $"adjust:{Guid.NewGuid()}",
                    Note = request.Reason.Trim(),
                    ActorUserId = actorUserId,
                    OccurredAtUtc = _dateTime.UtcNow
                });
                await _context.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                _context.ResetChangeTracker();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return await GetWalletsAsync(cancellationToken);
    }

    public async Task<PagedResult<PlatformCreditEntryDto>> GetWalletLedgerAsync(PlatformCreditEntryQuery query, CancellationToken cancellationToken = default)
    {
        var rows = _context.PlatformCreditEntries.AsNoTracking().AsQueryable();
        if (query.QuotaType is { } type)
            rows = rows.Where(e => e.QuotaType == type);

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var names = await TenantNamesAsync(page.Where(e => e.TenantId != null).Select(e => e.TenantId!.Value), cancellationToken);
        var items = page.Select(e => new PlatformCreditEntryDto(
            e.Id, e.QuotaType, e.EntryType, e.UnitsDelta, e.BalanceAfter, e.TenantId,
            e.TenantId is { } id ? names.GetValueOrDefault(id) : null, e.Note, e.OccurredAtUtc)).ToList();
        return new PagedResult<PlatformCreditEntryDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<PagedResult<CreditUsageDto>> GetUsageAsync(CreditUsageQuery query, CancellationToken cancellationToken = default)
    {
        var rows = _context.CreditUsages.AsNoTracking().AsQueryable();
        if (query.Scenario is { } scenario) rows = rows.Where(u => u.Scenario == scenario);
        if (query.ChargedTo is { } payer) rows = rows.Where(u => u.ChargedTo == payer);
        if (query.Status is { } status) rows = rows.Where(u => u.Status == status);
        if (query.TenantId is { } tenantId) rows = rows.Where(u => u.TenantId == tenantId);
        if (query.FellBackToTenant is { } fellBack) rows = rows.Where(u => u.FellBackToTenant == fellBack);
        if (query.FromUtc is { } from) rows = rows.Where(u => u.OccurredAtUtc >= from);
        if (query.ToUtc is { } to) rows = rows.Where(u => u.OccurredAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var matching = _context.Tenants.IgnoreQueryFilters().Where(t => t.Name.Contains(term)).Select(t => t.Id);
            rows = rows.Where(u => (u.Description != null && u.Description.Contains(term)) || matching.Contains(u.TenantId));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(u => u.OccurredAtUtc).ThenByDescending(u => u.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var names = await TenantNamesAsync(page.Select(u => u.TenantId), cancellationToken);
        var items = page.Select(u => ToDto(u, names.GetValueOrDefault(u.TenantId) ?? string.Empty)).ToList();
        return new PagedResult<CreditUsageDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<CreditUsageDto> RedeemToTenantAsync(Guid usageId, string note, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw Invalid(nameof(note), "Say why the credits are being given back - the tenant is told.");

        var usage = await _context.CreditUsages.FirstOrDefaultAsync(u => u.Id == usageId, cancellationToken)
            ?? throw new NotFoundException(nameof(CreditUsage), usageId);

        if (usage.ChargedTo != CreditPayer.Tenant)
            throw new ConflictException("The platform paid for this one, so the tenant has nothing to get back.");
        if (usage.Status != CreditUsageStatus.Charged)
            throw new ConflictException(usage.Status == CreditUsageStatus.Redeemed
                ? "These credits have already been given back to the tenant."
                : "This spend has already been returned or was never charged.");
        if (usage.Units <= 0)
            throw new ConflictException("There is nothing to give back.");

        var tenantId = usage.TenantId;
        var quotaType = usage.QuotaType;
        var units = usage.Units;
        var scenarioTitle = CreditScenarioCatalog.For(usage.Scenario).Title;
        var settledNote = note.Trim();

        // Claimed first, so two admins clicking at once cannot both give the credits back.
        usage.Status = CreditUsageStatus.Redeemed;
        usage.SettledAtUtc = _dateTime.UtcNow;
        usage.SettledByUserId = actorUserId;
        usage.SettlementNote = settledNote;
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            // A fresh grant rather than a reversal, so it still works when the credits the tenant spent have since expired.
            await _tenantLedger.AdjustAsync(tenantId, quotaType, units, $"Credits returned - {scenarioTitle}: {settledNote}", actorUserId, 365, cancellationToken);
        }
        catch
        {
            _context.ResetChangeTracker();
            var claimed = await _context.CreditUsages.FirstOrDefaultAsync(u => u.Id == usageId, cancellationToken);
            if (claimed is not null)
            {
                claimed.Status = CreditUsageStatus.Charged;
                claimed.SettledAtUtc = null;
                claimed.SettledByUserId = null;
                claimed.SettlementNote = null;
                await _context.SaveChangesAsync(CancellationToken.None);
            }

            throw;
        }

        var saved = await _context.CreditUsages.AsNoTracking().FirstAsync(u => u.Id == usageId, cancellationToken);
        var names = await TenantNamesAsync(new[] { saved.TenantId }, cancellationToken);
        return ToDto(saved, names.GetValueOrDefault(saved.TenantId) ?? string.Empty);
    }

    public async Task<PagedResult<TenantCreditUsageDto>> GetTenantUsageAsync(Guid tenantId, TenantCreditUsageQuery query, CancellationToken cancellationToken = default)
    {
        // Unpaid rows are the platform's own bookkeeping (nobody was charged), so the tenant does not see them.
        var rows = _context.CreditUsages.AsNoTracking().Where(u => u.TenantId == tenantId && u.Status != CreditUsageStatus.Unpaid);
        if (query.Scenario is { } scenario) rows = rows.Where(u => u.Scenario == scenario);
        if (query.CoveredByPlatform is { } covered)
            rows = covered ? rows.Where(u => u.ChargedTo == CreditPayer.Platform) : rows.Where(u => u.ChargedTo == CreditPayer.Tenant);
        if (query.FromUtc is { } from) rows = rows.Where(u => u.OccurredAtUtc >= from);
        if (query.ToUtc is { } to) rows = rows.Where(u => u.OccurredAtUtc < to);

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(u => u.OccurredAtUtc).ThenByDescending(u => u.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var items = page.Select(u => new TenantCreditUsageDto(
            u.Id, u.Scenario, CreditScenarioCatalog.For(u.Scenario).Title, u.QuotaType, u.Units,
            u.ChargedTo == CreditPayer.Platform, u.Status is CreditUsageStatus.Reversed or CreditUsageStatus.Redeemed,
            u.Description, u.OccurredAtUtc)).ToList();
        return new PagedResult<TenantCreditUsageDto>(items, total, query.Page, query.PageSize);
    }

    private async Task<Dictionary<Guid, string>> TenantNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        return distinct.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => distinct.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    private static CreditUsageDto ToDto(CreditUsage u, string tenantName) => new(
        u.Id, u.TenantId, tenantName, u.Scenario, CreditScenarioCatalog.For(u.Scenario).Title, u.QuotaType, u.Units,
        u.ConfiguredPayer, u.ChargedTo, u.FellBackToTenant, u.Status, u.Description, u.ReferenceType, u.ReferenceId,
        u.OccurredAtUtc, u.SettledAtUtc, u.SettlementNote);

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
