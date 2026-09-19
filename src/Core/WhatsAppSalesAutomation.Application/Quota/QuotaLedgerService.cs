using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>
/// See <see cref="IQuotaLedgerService"/>. The shape of every operation is the same: open the tenant's
/// "book" for one quota type (wallet, unprocessed grants, expiry applied), change grants and post
/// ledger entries against it, set the wallet from the running total, save once. The wallet row's
/// concurrency token turns a race into a retry rather than a double spend.
/// </summary>
public class QuotaLedgerService : IQuotaLedgerService
{
    public const int CreditValidityMonths = 12;

    private const int MaxAttempts = 5;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public QuotaLedgerService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<IReadOnlyList<QuotaBalanceDto>> GetBalancesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var live = await _context.QuotaGrants.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.ExpiredProcessedAtUtc == null && g.ExpiresAtUtc > now)
            .OrderBy(g => g.ExpiresAtUtc).ThenBy(g => g.Origin)
            .ToListAsync(cancellationToken);

        return Enum.GetValues<QuotaType>()
            .Select(type =>
            {
                var ofType = live.Where(g => g.QuotaType == type).ToList();
                var grants = ofType.Where(g => g.UnitsRemaining > 0)
                    .Select(g => new QuotaGrantDto(g.Id, g.Origin, g.UnitsGranted, g.UnitsRemaining, g.ExpiresAtUtc))
                    .ToList();
                return new QuotaBalanceDto(type, grants.Sum(g => g.UnitsRemaining), ofType.Sum(g => g.UnitsGranted), grants);
            })
            .ToList();
    }

    public Task AllocatePlanQuotaAsync(Guid tenantId, Guid planId, DateTime periodStartUtc, DateTime periodEndUtc, Guid? paymentId, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var now = _dateTime.UtcNow;
            var quotas = await _context.PlanQuotas.Where(q => q.PlanId == planId).ToListAsync(cancellationToken);

            foreach (var quota in quotas)
            {
                // The plan is part of the key: two different plans allocated in the same second must both land.
                var operationKey = $"alloc:{planId:N}:{periodStartUtc:yyyyMMddTHHmmss}:{quota.QuotaType}";
                if (await HasEntryAsync(tenantId, operationKey, QuotaEntryType.Allocation, cancellationToken))
                    continue;

                var book = await OpenAsync(tenantId, quota.QuotaType, now, cancellationToken);
                var grant = NewGrant(tenantId, quota.QuotaType, QuotaGrantOrigin.PlanAllocation, quota.IncludedUnits, now, periodEndUtc, paymentId);
                _context.QuotaGrants.Add(grant);
                book.Grants.Add(grant);
                Post(book, QuotaEntryType.Allocation, quota.IncludedUnits, grant.Id, operationKey, "Payment", paymentId?.ToString(), null, null, now);
                book.Wallet.Balance = book.Running;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task GrantTrialQuotaAsync(Guid tenantId, IReadOnlyDictionary<QuotaType, decimal> units, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var now = _dateTime.UtcNow;
            foreach (var (type, amount) in units)
            {
                var operationKey = $"trial:{type}";
                if (amount <= 0 || await HasEntryAsync(tenantId, operationKey, QuotaEntryType.Allocation, cancellationToken))
                    continue;

                var book = await OpenAsync(tenantId, type, now, cancellationToken);
                var grant = NewGrant(tenantId, type, QuotaGrantOrigin.Trial, amount, now, expiresAtUtc, null);
                _context.QuotaGrants.Add(grant);
                book.Grants.Add(grant);
                Post(book, QuotaEntryType.Allocation, amount, grant.Id, operationKey, "Trial", null, "Free trial allocation", null, now);
                book.Wallet.Balance = book.Running;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task GrantCreditsAsync(Guid tenantId, CreditPack pack, Guid paymentId, DateTime purchasedAtUtc, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var operationKey = $"purchase:{paymentId}";
            if (await HasEntryAsync(tenantId, operationKey, QuotaEntryType.Purchase, cancellationToken))
                return true;

            var now = _dateTime.UtcNow;
            var book = await OpenAsync(tenantId, pack.QuotaType, now, cancellationToken);
            var grant = NewGrant(tenantId, pack.QuotaType, QuotaGrantOrigin.CreditPurchase, pack.Units, purchasedAtUtc, purchasedAtUtc.AddMonths(CreditValidityMonths), paymentId);
            _context.QuotaGrants.Add(grant);
            book.Grants.Add(grant);
            Post(book, QuotaEntryType.Purchase, pack.Units, grant.Id, operationKey, "Payment", paymentId.ToString(), pack.Name, null, now);
            book.Wallet.Balance = book.Running;

            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public async Task<ConsumeResult> ConsumeAsync(ConsumeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Units <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Units must be positive.");

        return await WithRetryAsync(async () =>
        {
            var previous = await _context.QuotaLedgerEntries.IgnoreQueryFilters()
                .Where(e => e.TenantId == request.TenantId && e.OperationKey == request.OperationKey && e.EntryType == QuotaEntryType.Consumption)
                .ToListAsync(cancellationToken);
            if (previous.Count > 0)
            {
                var already = -previous.Sum(e => e.UnitsDelta);
                return new ConsumeResult(request.Units, already, previous.OrderBy(e => e.OccurredAtUtc).Last().BalanceAfter, already >= request.Units, AlreadyApplied: true);
            }

            var now = _dateTime.UtcNow;
            var book = await OpenAsync(request.TenantId, request.QuotaType, now, cancellationToken);
            var available = book.Live(now).Sum(g => g.UnitsRemaining);
            var take = request.AllowPartial ? Math.Min(request.Units, available) : (available >= request.Units ? request.Units : 0m);

            if (take > 0)
                Draw(book, take, QuotaEntryType.Consumption, request.OperationKey, request.ReferenceType, request.ReferenceId, request.Note, null, now);

            book.Wallet.Balance = book.Running;
            await _context.SaveChangesAsync(cancellationToken);

            return new ConsumeResult(request.Units, take, book.Running, take >= request.Units, AlreadyApplied: false);
        }, cancellationToken);
    }

    public Task<bool> ReverseConsumptionAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var reversalKey = $"rev:{operationKey}";
            if (await HasEntryAsync(tenantId, reversalKey, QuotaEntryType.ConsumptionReversal, cancellationToken))
                return true;

            var spent = await _context.QuotaLedgerEntries.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && e.OperationKey == operationKey && e.EntryType == QuotaEntryType.Consumption)
                .ToListAsync(cancellationToken);
            if (spent.Count == 0)
                return false;

            var now = _dateTime.UtcNow;
            var book = await OpenAsync(tenantId, spent[0].QuotaType, now, cancellationToken);
            foreach (var entry in spent)
            {
                // A grant that has expired since is gone - the units were never going to be usable.
                var grant = book.Grants.FirstOrDefault(g => g.Id == entry.GrantId);
                if (grant is null)
                    continue;

                var back = -entry.UnitsDelta;
                grant.UnitsRemaining += back;
                Post(book, QuotaEntryType.ConsumptionReversal, back, grant.Id, reversalKey, entry.ReferenceType, entry.ReferenceId, "Reversal of " + operationKey, null, now);
            }

            book.Wallet.Balance = book.Running;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task AdjustAsync(Guid tenantId, QuotaType quotaType, decimal unitsDelta, string reason, Guid actorUserId, int validForDays = 365, CancellationToken cancellationToken = default)
    {
        if (unitsDelta == 0)
            throw new ArgumentOutOfRangeException(nameof(unitsDelta), "An adjustment must change something.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An adjustment needs a reason.", nameof(reason));

        var operationKey = $"adjust:{Guid.NewGuid()}";

        return WithRetryAsync(async () =>
        {
            var now = _dateTime.UtcNow;
            var book = await OpenAsync(tenantId, quotaType, now, cancellationToken);

            if (unitsDelta > 0)
            {
                var grant = NewGrant(tenantId, quotaType, QuotaGrantOrigin.Adjustment, unitsDelta, now, now.AddDays(validForDays), null);
                _context.QuotaGrants.Add(grant);
                book.Grants.Add(grant);
                Post(book, QuotaEntryType.AdjustmentCredit, unitsDelta, grant.Id, operationKey, "Admin", null, reason, actorUserId, now);
            }
            else
            {
                var take = -unitsDelta;
                if (book.Live(now).Sum(g => g.UnitsRemaining) < take)
                    throw new ConflictException("The tenant doesn't have that many units left to remove.");
                Draw(book, take, QuotaEntryType.AdjustmentDebit, operationKey, "Admin", null, reason, actorUserId, now);
            }

            book.Wallet.Balance = book.Running;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<decimal> ClawBackCreditsAsync(Guid tenantId, Guid paymentId, decimal units, string reason, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var operationKey = $"clawback:{Guid.NewGuid()}";

        return await WithRetryAsync(async () =>
        {
            var target = await _context.QuotaGrants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.PaymentId == paymentId && g.Origin == QuotaGrantOrigin.CreditPurchase, cancellationToken);
            if (target is null)
                return 0m;

            var now = _dateTime.UtcNow;
            var book = await OpenAsync(tenantId, target.QuotaType, now, cancellationToken);
            var grant = book.Grants.FirstOrDefault(g => g.Id == target.Id);
            var take = grant is null ? 0m : Math.Min(units, grant.UnitsRemaining);
            if (grant is null || take <= 0)
                return 0m;

            grant.UnitsRemaining -= take;
            Post(book, QuotaEntryType.RefundClawback, -take, grant.Id, operationKey, "Payment", paymentId.ToString(), reason, actorUserId, now);
            book.Wallet.Balance = book.Running;
            await _context.SaveChangesAsync(cancellationToken);
            return take;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentGrantUsage>> GetPaymentGrantUsageAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var grants = await _context.QuotaGrants.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.PaymentId == paymentId)
            .ToListAsync(cancellationToken);

        return grants
            .Select(g => new PaymentGrantUsage(
                g.QuotaType, g.Origin, g.UnitsGranted,
                g.ExpiredProcessedAtUtc is null && g.ExpiresAtUtc > now ? g.UnitsRemaining : 0m,
                g.ExpiresAtUtc))
            .ToList();
    }

    public Task<decimal> HoldForRefundAsync(Guid tenantId, Guid paymentId, Guid refundRequestId, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var operationKey = $"refund-hold:{refundRequestId}";
            var existing = await _context.QuotaLedgerEntries.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && e.OperationKey == operationKey && e.EntryType == QuotaEntryType.RefundHold)
                .ToListAsync(cancellationToken);
            if (existing.Count > 0)
                return -existing.Sum(e => e.UnitsDelta);

            var now = _dateTime.UtcNow;
            var targets = await _context.QuotaGrants.IgnoreQueryFilters()
                .Where(g => g.TenantId == tenantId && g.PaymentId == paymentId)
                .Select(g => new { g.Id, g.QuotaType })
                .ToListAsync(cancellationToken);

            var held = 0m;
            foreach (var type in targets.Select(t => t.QuotaType).Distinct())
            {
                var book = await OpenAsync(tenantId, type, now, cancellationToken);
                foreach (var target in targets.Where(t => t.QuotaType == type))
                {
                    var grant = book.Grants.FirstOrDefault(g => g.Id == target.Id);
                    if (grant is null || grant.UnitsRemaining <= 0)
                        continue;

                    var take = grant.UnitsRemaining;
                    grant.UnitsRemaining = 0;
                    held += take;
                    Post(book, QuotaEntryType.RefundHold, -take, grant.Id, operationKey, "RefundRequest", refundRequestId.ToString(), "Held while a refund request is reviewed", null, now);
                }

                book.Wallet.Balance = book.Running;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return held;
        }, cancellationToken);

    public Task<decimal> ReleaseRefundHoldAsync(Guid tenantId, Guid refundRequestId, decimal keepFraction, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var holdKey = $"refund-hold:{refundRequestId}";
            var releaseKey = $"refund-release:{refundRequestId}";
            if (await HasEntryAsync(tenantId, releaseKey, QuotaEntryType.RefundRelease, cancellationToken))
                return 0m;

            var holds = await _context.QuotaLedgerEntries.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && e.OperationKey == holdKey && e.EntryType == QuotaEntryType.RefundHold)
                .ToListAsync(cancellationToken);
            if (holds.Count == 0)
                return 0m;

            var now = _dateTime.UtcNow;
            var keep = Math.Clamp(keepFraction, 0m, 1m);
            var released = 0m;

            foreach (var type in holds.Select(h => h.QuotaType).Distinct())
            {
                var book = await OpenAsync(tenantId, type, now, cancellationToken);
                foreach (var hold in holds.Where(h => h.QuotaType == type))
                {
                    // A grant that has expired since is gone - those units were never going to be usable again.
                    var grant = book.Grants.FirstOrDefault(g => g.Id == hold.GrantId);
                    var back = Math.Round(-hold.UnitsDelta * (1m - keep), 4);
                    if (grant is null || back <= 0)
                        continue;

                    grant.UnitsRemaining += back;
                    released += back;
                    Post(book, QuotaEntryType.RefundRelease, back, grant.Id, releaseKey, "RefundRequest", refundRequestId.ToString(), keep == 0m ? "Refund not going ahead" : "Unapproved share of a partial refund", null, now);
                }

                book.Wallet.Balance = book.Running;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return released;
        }, cancellationToken);

    public Task<decimal> ForfeitPlanAllocationsAsync(Guid tenantId, string reason, CancellationToken cancellationToken = default) =>
        WithRetryAsync(async () =>
        {
            var now = _dateTime.UtcNow;
            var forfeited = 0m;

            foreach (var type in Enum.GetValues<QuotaType>())
            {
                var book = await OpenAsync(tenantId, type, now, cancellationToken);
                foreach (var grant in book.Grants.Where(g => g.Origin == QuotaGrantOrigin.PlanAllocation).ToList())
                {
                    if (grant.UnitsRemaining > 0)
                    {
                        var lost = grant.UnitsRemaining;
                        grant.UnitsRemaining = 0;
                        forfeited += lost;
                        Post(book, QuotaEntryType.Expiry, -lost, grant.Id, $"forfeit:{grant.Id}", null, null, reason, null, now);
                    }

                    grant.ExpiredProcessedAtUtc = now;
                    book.Grants.Remove(grant);
                }

                book.Wallet.Balance = book.Running;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return forfeited;
        }, cancellationToken);

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var due = await _context.QuotaGrants.IgnoreQueryFilters()
            .Where(g => g.ExpiredProcessedAtUtc == null && g.ExpiresAtUtc <= now)
            .Select(g => new { g.TenantId, g.QuotaType })
            .Distinct()
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var pair in due)
        {
            // Opening the book applies the expiry - that is all this pass has to do for each pair.
            processed += await WithRetryAsync(async () =>
            {
                var book = await OpenAsync(pair.TenantId, pair.QuotaType, _dateTime.UtcNow, cancellationToken);
                book.Wallet.Balance = book.Running;
                await _context.SaveChangesAsync(cancellationToken);
                return book.ExpiredCount;
            }, cancellationToken);
        }

        return processed;
    }

    public async Task<PagedResult<QuotaLedgerEntryDto>> GetLedgerAsync(Guid tenantId, QuotaLedgerQuery query, CancellationToken cancellationToken = default)
    {
        var entries = _context.QuotaLedgerEntries.IgnoreQueryFilters().Where(e => e.TenantId == tenantId);
        if (query.QuotaType is { } type)
            entries = entries.Where(e => e.QuotaType == type);

        var total = await entries.CountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(e => new QuotaLedgerEntryDto(
                e.Id, e.QuotaType, e.EntryType, e.UnitsDelta, e.BalanceAfter, e.GrantId, e.ReferenceType, e.ReferenceId, e.Note, e.OccurredAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResult<QuotaLedgerEntryDto>(items, total, query.Page, query.PageSize);
    }

    /// <summary>The working set for one tenant and quota type: the wallet, every grant not yet written
    /// off (so <see cref="Running"/> starts equal to the ledger's own sum), and the running balance. Opening
    /// it writes off anything already past its expiry, so every later step sees only usable grants.</summary>
    private sealed class Book
    {
        public required QuotaWallet Wallet { get; init; }
        public required List<QuotaGrant> Grants { get; init; }
        public decimal Running { get; set; }
        public int ExpiredCount { get; set; }

        public IEnumerable<QuotaGrant> Live(DateTime now) =>
            Grants.Where(g => g.UnitsRemaining > 0 && g.ExpiresAtUtc > now)
                  .OrderBy(g => g.ExpiresAtUtc).ThenBy(g => g.Origin);
    }

    private async Task<Book> OpenAsync(Guid tenantId, QuotaType type, DateTime now, CancellationToken cancellationToken)
    {
        var wallet = await _context.QuotaWallets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.QuotaType == type, cancellationToken);
        if (wallet is null)
        {
            wallet = new QuotaWallet { TenantId = tenantId, QuotaType = type };
            _context.QuotaWallets.Add(wallet);
        }

        var grants = await _context.QuotaGrants.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.QuotaType == type && g.ExpiredProcessedAtUtc == null)
            .ToListAsync(cancellationToken);

        var book = new Book { Wallet = wallet, Grants = grants, Running = grants.Sum(g => g.UnitsRemaining) };

        foreach (var grant in grants.Where(g => g.ExpiresAtUtc <= now).ToList())
        {
            if (grant.UnitsRemaining > 0)
            {
                var lost = grant.UnitsRemaining;
                grant.UnitsRemaining = 0;
                Post(book, QuotaEntryType.Expiry, -lost, grant.Id, $"expire:{grant.Id}", null, null, null, null, now);
            }

            grant.ExpiredProcessedAtUtc = now;
            book.Grants.Remove(grant);
            book.ExpiredCount++;
        }

        return book;
    }

    /// <summary>Takes <paramref name="units"/> from the live grants, soonest-expiring first (included plan
    /// quota before purchased credits on a tie), one ledger entry per grant touched.</summary>
    private void Draw(Book book, decimal units, QuotaEntryType entryType, string operationKey, string? referenceType, string? referenceId, string? note, Guid? actor, DateTime now)
    {
        var left = units;
        foreach (var grant in book.Live(now).ToList())
        {
            if (left <= 0)
                break;

            var take = Math.Min(grant.UnitsRemaining, left);
            grant.UnitsRemaining -= take;
            left -= take;
            Post(book, entryType, -take, grant.Id, operationKey, referenceType, referenceId, note, actor, now);
        }
    }

    private void Post(Book book, QuotaEntryType type, decimal delta, Guid? grantId, string operationKey, string? referenceType, string? referenceId, string? note, Guid? actor, DateTime now)
    {
        book.Running += delta;
        _context.QuotaLedgerEntries.Add(new QuotaLedgerEntry
        {
            TenantId = book.Wallet.TenantId,
            QuotaType = book.Wallet.QuotaType,
            EntryType = type,
            UnitsDelta = delta,
            BalanceAfter = book.Running,
            GrantId = grantId,
            OperationKey = operationKey,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            Note = note,
            ActorUserId = actor,
            OccurredAtUtc = now
        });
    }

    private static QuotaGrant NewGrant(Guid tenantId, QuotaType type, QuotaGrantOrigin origin, decimal units, DateTime grantedAt, DateTime expiresAt, Guid? paymentId) => new()
    {
        TenantId = tenantId,
        QuotaType = type,
        Origin = origin,
        UnitsGranted = units,
        UnitsRemaining = units,
        GrantedAtUtc = grantedAt,
        ExpiresAtUtc = expiresAt,
        PaymentId = paymentId
    };

    private Task<bool> HasEntryAsync(Guid tenantId, string operationKey, QuotaEntryType type, CancellationToken cancellationToken) =>
        _context.QuotaLedgerEntries.IgnoreQueryFilters()
            .AnyAsync(e => e.TenantId == tenantId && e.OperationKey == operationKey && e.EntryType == type, cancellationToken);

    /// <summary>Runs one operation, and on a lost race (another writer moved the wallet first, or created
    /// it first) throws away what was read and does it again against fresh data.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                _context.ResetChangeTracker();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
