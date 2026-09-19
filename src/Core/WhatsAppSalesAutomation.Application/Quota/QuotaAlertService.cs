using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>
/// Looks at every tenant's live quota and raises the alerts it has earned: running low (20% and 5% left),
/// used up, and purchased credits about to expire. Meant to run every few minutes - it decides nothing
/// stateful itself, because <see cref="ITenantNotifier"/> refuses to raise the same (kind, quota, episode)
/// twice. An "episode" ends whenever units are added (a renewal, a purchase, an adjustment), which is what
/// lets the same threshold alert again the next time the tenant runs low.
/// </summary>
public interface IQuotaAlertService
{
    /// <summary>Returns how many alerts were raised.</summary>
    Task<int> EvaluateAsync(CancellationToken cancellationToken = default);
}

public class QuotaAlertService : IQuotaAlertService
{
    private const decimal LowFraction = 0.20m;
    private const decimal VeryLowFraction = 0.05m;

    private static readonly QuotaEntryType[] AddsUnits =
    {
        QuotaEntryType.Allocation, QuotaEntryType.Purchase, QuotaEntryType.AdjustmentCredit
    };

    private readonly IApplicationDbContext _context;
    private readonly ITenantNotifier _notifier;
    private readonly IDateTimeProvider _dateTime;

    public QuotaAlertService(IApplicationDbContext context, ITenantNotifier notifier, IDateTimeProvider dateTime)
    {
        _context = context;
        _notifier = notifier;
        _dateTime = dateTime;
    }

    public async Task<int> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        var live = await _context.QuotaGrants.IgnoreQueryFilters()
            .Where(g => g.ExpiredProcessedAtUtc == null && g.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        if (live.Count == 0)
            return 0;

        // Only tenants that are actually being served get alerts.
        var tenantIds = live.Select(g => g.TenantId).Distinct().ToList();
        var servedTenants = (await _context.Tenants
                .Where(t => tenantIds.Contains(t.Id) && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Trial))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var raised = 0;

        foreach (var pair in live.Where(g => servedTenants.Contains(g.TenantId)).GroupBy(g => (g.TenantId, g.QuotaType)))
        {
            var (tenantId, type) = pair.Key;
            var capacity = pair.Sum(g => g.UnitsGranted);
            var remaining = pair.Sum(g => g.UnitsRemaining);
            if (capacity <= 0)
                continue;

            var fraction = remaining / capacity;
            var kind = remaining <= 0 ? TenantNotificationKind.QuotaExhausted
                : fraction <= VeryLowFraction ? TenantNotificationKind.QuotaLow5
                : fraction <= LowFraction ? TenantNotificationKind.QuotaLow20
                : (TenantNotificationKind?)null;

            if (kind is { } k)
            {
                var episode = await CurrentEpisodeAsync(tenantId, type, cancellationToken);
                var (title, body) = Describe(k, type, remaining, capacity);
                if (await _notifier.NotifyAsync(new TenantNotificationRequest(tenantId, k, type, episode, title, body, AlsoWhatsApp: true), cancellationToken))
                    raised++;
            }
        }

        // Purchased credits that will lapse unused - the 3-day warning supersedes the 14-day one.
        foreach (var grant in live.Where(g => g.Origin == QuotaGrantOrigin.CreditPurchase && g.UnitsRemaining > 0 && servedTenants.Contains(g.TenantId)))
        {
            var daysLeft = (grant.ExpiresAtUtc - now).TotalDays;
            var kind = daysLeft <= 3 ? TenantNotificationKind.CreditsExpiring3
                : daysLeft <= 14 ? TenantNotificationKind.CreditsExpiring14
                : (TenantNotificationKind?)null;
            if (kind is not { } k)
                continue;

            var label = Label(grant.QuotaType);
            var when = daysLeft <= 3 ? "in the next 3 days" : "in the next 14 days";
            var title = $"Some of your {label} credits expire soon";
            var body = $"{Number(grant.UnitsRemaining)} of your purchased {label} credits expire {when}, on {grant.ExpiresAtUtc:d MMM yyyy}. Use them before then.";

            if (await _notifier.NotifyAsync(new TenantNotificationRequest(grant.TenantId, k, grant.QuotaType, grant.Id.ToString(), title, body, AlsoWhatsApp: true), cancellationToken))
                raised++;
        }

        return raised;
    }

    /// <summary>The id of the last entry that added units for this tenant and quota type: a new one means a
    /// top-up or renewal happened, so the thresholds are worth alerting on afresh.</summary>
    private async Task<string> CurrentEpisodeAsync(Guid tenantId, QuotaType type, CancellationToken cancellationToken)
    {
        var last = await _context.QuotaLedgerEntries.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.QuotaType == type && AddsUnits.Contains(e.EntryType))
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.CreatedAt)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return last?.ToString() ?? "none";
    }

    private static (string Title, string Body) Describe(TenantNotificationKind kind, QuotaType type, decimal remaining, decimal capacity)
    {
        var label = Label(type);
        return kind switch
        {
            TenantNotificationKind.QuotaExhausted => (
                $"You've used all your {label}",
                $"Your {label} are used up, so anything that needs them is paused. Buy credits or wait for your plan to renew to carry on."),
            TenantNotificationKind.QuotaLow5 => (
                $"Your {label} are almost gone",
                $"Only {Number(remaining)} of {Number(capacity)} {label} are left (under 5%). Buy credits now to avoid an interruption."),
            _ => (
                $"Your {label} are running low",
                $"{Number(remaining)} of {Number(capacity)} {label} are left (under 20%). Buy credits to keep going without a break.")
        };
    }

    private static string Label(QuotaType type) => type switch
    {
        QuotaType.WhatsAppMessages => "WhatsApp messages",
        QuotaType.AiConversations => "AI conversations",
        _ => "lead candidates"
    };

    private static string Number(decimal value) => value.ToString("#,##0.##");
}
