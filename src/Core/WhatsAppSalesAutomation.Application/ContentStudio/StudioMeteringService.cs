using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.ContentStudio;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

/// <summary>See <see cref="IStudioMeteringService"/>.</summary>
public class StudioMeteringService : IStudioMeteringService
{
    private const int OverviewDays = 30;
    private const int MaxText = 300;

    private readonly IApplicationDbContext _context;
    private readonly IQuotaLedgerService _ledger;
    private readonly IQuotaAlertService _alerts;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantConfigOverrideProvider _config;

    public StudioMeteringService(
        IApplicationDbContext context,
        IQuotaLedgerService ledger,
        IQuotaAlertService alerts,
        IDateTimeProvider dateTime,
        ITenantConfigOverrideProvider config)
    {
        _context = context;
        _ledger = ledger;
        _alerts = alerts;
        _dateTime = dateTime;
        _config = config;
    }

    public async Task<StudioChargeResult> ChargeAsync(StudioChargeRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationKey))
            throw new ArgumentException("An operation key is required so a retry cannot charge twice.", nameof(request));

        var operationKey = OperationKeyFor(request.OperationKey);

        var existing = await _context.StudioActivities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == request.TenantId && a.OperationKey == operationKey, cancellationToken);
        if (existing is not null)
            return new StudioChargeResult(existing.Status != StudioActivityStatus.Blocked, existing.Id, AlreadyCharged: true, null);

        var rule = StudioChargeCatalog.For(request.Kind);
        var units = request.Units ?? rule.DefaultUnits;
        if (units < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Units cannot be negative.");

        var now = _dateTime.UtcNow;
        var activity = new StudioActivity
        {
            TenantId = request.TenantId,
            Kind = request.Kind,
            QuotaType = rule.QuotaType,
            Description = Truncate(request.Description, MaxText),
            OperationKey = operationKey,
            ReferenceType = request.ReferenceType,
            ReferenceId = request.ReferenceId,
            Provider = request.Provider,
            ActorUserId = request.ActorUserId,
            RequestedAtUtc = now
        };

        if (units > 0)
        {
            var spent = await _ledger.ConsumeAsync(
                new ConsumeRequest(request.TenantId, rule.QuotaType, units, operationKey, "StudioActivity", activity.Id.ToString(), rule.Label),
                cancellationToken);

            if (!spent.Sufficient)
            {
                var label = QuotaAlertService.Label(rule.QuotaType);
                activity.Status = StudioActivityStatus.Blocked;
                activity.Units = 0;
                activity.BalanceAfter = spent.BalanceAfter;
                activity.Detail = $"Not enough {label} left. Needed {Format(units)}, had {Format(spent.BalanceAfter)}.";
                activity.CompletedAtUtc = now;
                _context.StudioActivities.Add(activity);
                await _context.SaveChangesAsync(cancellationToken);
                await _alerts.NotifyIfExhaustedAsync(request.TenantId, rule.QuotaType, cancellationToken);
                return new StudioChargeResult(false, activity.Id, false, $"You don't have enough {label} left for this. Buy credits to continue.");
            }

            activity.Units = units;
            activity.BalanceAfter = spent.BalanceAfter;
        }
        else
        {
            activity.BalanceAfter = (await _ledger.GetBalancesAsync(request.TenantId, cancellationToken))
                .Single(b => b.QuotaType == rule.QuotaType).Balance;
        }

        activity.Status = StudioActivityStatus.Charged;
        _context.StudioActivities.Add(activity);
        await _context.SaveChangesAsync(cancellationToken);
        return new StudioChargeResult(true, activity.Id, false, null);
    }

    public async Task CompleteAsync(Guid activityId, CancellationToken cancellationToken = default)
    {
        var activity = await FindAsync(activityId, cancellationToken);
        if (activity.Status != StudioActivityStatus.Charged)
            return;

        activity.Status = StudioActivityStatus.Completed;
        activity.CompletedAtUtc = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RefundAsync(Guid activityId, string reason, CancellationToken cancellationToken = default)
    {
        var activity = await FindAsync(activityId, cancellationToken);
        if (activity.Status is StudioActivityStatus.Refunded or StudioActivityStatus.Blocked)
            return;

        if (activity.Units > 0)
            await _ledger.ReverseConsumptionAsync(activity.TenantId, activity.OperationKey, cancellationToken);

        activity.UnitsRefunded = activity.Units;
        activity.Status = StudioActivityStatus.Refunded;
        activity.Detail = Truncate(reason, MaxText);
        activity.CompletedAtUtc = _dateTime.UtcNow;
        if (activity.Units > 0)
        {
            activity.BalanceAfter = (await _ledger.GetBalancesAsync(activity.TenantId, cancellationToken))
                .Single(b => b.QuotaType == activity.QuotaType).Balance;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<StudioActivityDto>> GetActivityAsync(Guid tenantId, StudioActivityQuery query, CancellationToken cancellationToken = default)
    {
        var rows = _context.StudioActivities.IgnoreQueryFilters().Where(a => a.TenantId == tenantId);

        if (query.Kind is { } kind) rows = rows.Where(a => a.Kind == kind);
        if (query.Status is { } status) rows = rows.Where(a => a.Status == status);
        if (query.QuotaType is { } quota) rows = rows.Where(a => a.QuotaType == quota);
        if (query.FromUtc is { } from) rows = rows.Where(a => a.RequestedAtUtc >= from);
        if (query.ToUtc is { } to) rows = rows.Where(a => a.RequestedAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(a => a.Description.Contains(term) || (a.Provider != null && a.Provider.Contains(term)));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(a => a.RequestedAtUtc).ThenByDescending(a => a.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var actorIds = page.Where(a => a.ActorUserId != null).Select(a => a.ActorUserId!.Value).Distinct().ToList();
        var names = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users.IgnoreQueryFilters().Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Email ?? string.Empty, cancellationToken);

        var items = page.Select(a => ToDto(a, a.ActorUserId is { } id && names.TryGetValue(id, out var name) ? name : null)).ToList();
        return new PagedResult<StudioActivityDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<StudioOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var balances = (await _ledger.GetBalancesAsync(tenantId, cancellationToken))
            .Where(b => StudioChargeCatalog.QuotaTypes.Contains(b.QuotaType))
            .ToList();

        var since = _dateTime.UtcNow.AddDays(-OverviewDays);
        var recent = await _context.StudioActivities.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.RequestedAtUtc >= since)
            .Select(a => new { a.QuotaType, a.Status, a.Units, a.UnitsRefunded })
            .ToListAsync(cancellationToken);

        var totals = StudioChargeCatalog.QuotaTypes.Select(type =>
        {
            var ofType = recent.Where(a => a.QuotaType == type).ToList();
            var charged = ofType.Sum(a => a.Units);
            var refunded = ofType.Sum(a => a.UnitsRefunded);
            return new StudioUsageTotalDto(type, charged, refunded, charged - refunded, ofType.Count, ofType.Count(a => a.Status == StudioActivityStatus.Blocked));
        }).ToList();

        var settings = await _context.TenantStudioSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        var studioOptions = await _config.GetStudioOptionsForTenantAsync(tenantId, cancellationToken);
        return new StudioOverviewDto(studioOptions.Enabled, settings?.ApprovalPolicy ?? StudioApprovalPolicy.AdminOnly, balances, totals);
    }

    /// <summary>The ledger's operation key, namespaced so a studio key can never collide with another module's.</summary>
    public static string OperationKeyFor(string operationKey) => $"studio:{operationKey.Trim()}";

    private async Task<StudioActivity> FindAsync(Guid activityId, CancellationToken cancellationToken) =>
        await _context.StudioActivities.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == activityId, cancellationToken)
        ?? throw new NotFoundException("Studio activity not found.");

    private static StudioActivityDto ToDto(StudioActivity a, string? actorName) => new(
        a.Id, a.Kind, StudioChargeCatalog.LabelFor(a.Kind), a.Status, a.Description, a.QuotaType, a.Units, a.UnitsRefunded,
        a.Units - a.UnitsRefunded, a.BalanceAfter, a.OperationKey, a.ReferenceType, a.ReferenceId, a.Provider, a.Detail,
        a.ActorUserId, actorName, a.RequestedAtUtc, a.CompletedAtUtc);

    private static string Format(decimal value) => value.ToString("#,##0.##");

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
