using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Infrastructure.LeadDiscovery;

/// <summary>
/// The lead-discovery distributed lock, as a lease row in the database every application instance shares.
/// Each operation is a single conditional UPDATE - an atomic compare-and-set - so two instances can never
/// both believe they hold the same key: acquire only matches a free or expired row, renew and release only
/// match the exact (key, execution, token, owner) with a lease that has not yet run out.
///
/// Every call runs in its own scope and DbContext. The lease heartbeat calls in from a background thread
/// while the orchestrator is mid-transaction on its own context, and the lock row must never be part of
/// that business transaction (a rolled-back customer must not roll back a renewal). The scope has no tenant
/// set, so tenant-owned reads here use IgnoreQueryFilters with the tenant matched explicitly.
///
/// Timestamps come from the application clock. Instances are expected to be NTP-synchronised; the lease
/// duration is minutes, so ordinary skew of a second or two is irrelevant.
/// </summary>
public class SqlLeadDiscoveryLockStore : ILeadDiscoveryLockStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _clock;

    public SqlLeadDiscoveryLockStore(IServiceScopeFactory scopeFactory, IDateTimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
    }

    public async Task<LeadDiscoveryLockAcquireResult> TryAcquireAsync(
        LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var expires = now + lease;
        var key = claim.LockKey;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Take over a row that is free: released, or still marked Acquired but past its lease.
        var taken = await db.LeadDiscoveryLocks
            .Where(l => l.LockKey == key && (l.Status != LeadDiscoveryLockStatus.Acquired || l.ExpiresAtUtc <= now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(l => l.ExecutionId, claim.ExecutionId)
                .SetProperty(l => l.LockToken, claim.LockToken)
                .SetProperty(l => l.OwnerInstanceId, claim.OwnerInstanceId)
                .SetProperty(l => l.TenantId, claim.TenantId)
                .SetProperty(l => l.LeadDiscoveryProfileId, claim.ProfileId)
                .SetProperty(l => l.AcquiredAtUtc, now)
                .SetProperty(l => l.ExpiresAtUtc, expires)
                .SetProperty(l => l.LastRenewedAtUtc, (DateTime?)null)
                .SetProperty(l => l.Status, LeadDiscoveryLockStatus.Acquired), cancellationToken);

        if (taken == 1)
            return new LeadDiscoveryLockAcquireResult(true, expires, null);

        var holder = await db.LeadDiscoveryLocks.AsNoTracking()
            .Where(l => l.LockKey == key)
            .Select(l => (Guid?)l.ExecutionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (holder is not null)
            return new LeadDiscoveryLockAcquireResult(false, null, holder);

        // First use of this key. The primary key on LockKey decides a race between two first-time inserts.
        db.LeadDiscoveryLocks.Add(new LeadDiscoveryLock
        {
            LockKey = key,
            TenantId = claim.TenantId,
            LeadDiscoveryProfileId = claim.ProfileId,
            ExecutionId = claim.ExecutionId,
            LockToken = claim.LockToken,
            OwnerInstanceId = claim.OwnerInstanceId,
            AcquiredAtUtc = now,
            ExpiresAtUtc = expires,
            Status = LeadDiscoveryLockStatus.Acquired
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new LeadDiscoveryLockAcquireResult(true, expires, null);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.LeadDiscoveryLocks.AsNoTracking()
                .Where(l => l.LockKey == key)
                .Select(l => (Guid?)l.ExecutionId)
                .FirstOrDefaultAsync(cancellationToken);
            return new LeadDiscoveryLockAcquireResult(false, null, winner);
        }
    }

    public async Task<LeadDiscoveryLockOperationResult> RenewAsync(
        LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var expires = now + lease;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var renewed = await OwnedLiveLease(db, claim, now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(l => l.ExpiresAtUtc, expires)
                .SetProperty(l => l.LastRenewedAtUtc, now), cancellationToken);

        return renewed == 1
            ? new LeadDiscoveryLockOperationResult(LeadDiscoveryLockOwnership.Owned, expires)
            : new LeadDiscoveryLockOperationResult(await ClassifyFailureAsync(db, claim, cancellationToken), null);
    }

    public async Task<LeadDiscoveryLockOperationResult> ReleaseAsync(
        LeadDiscoveryLockClaim claim, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var released = await OwnedLiveLease(db, claim, now)
            .ExecuteUpdateAsync(set => set.SetProperty(l => l.Status, LeadDiscoveryLockStatus.Released), cancellationToken);

        return released == 1
            ? new LeadDiscoveryLockOperationResult(LeadDiscoveryLockOwnership.Owned, null)
            : new LeadDiscoveryLockOperationResult(await ClassifyFailureAsync(db, claim, cancellationToken), null);
    }

    public async Task RecordTransitionAsync(LeadDiscoveryLockTransitionRecord record, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.LeadDiscoveryLockTransitions.Add(new LeadDiscoveryLockTransition
        {
            TenantId = record.TenantId,
            ExecutionId = record.ExecutionId,
            LeadDiscoveryProfileId = record.ProfileId,
            ProcessingDate = record.ProcessingDate,
            FromStatus = record.From,
            ToStatus = record.To,
            TransitionAtUtc = record.AtUtc,
            LockTokenReference = record.TokenReference,
            OwnerInstanceId = record.OwnerInstanceId,
            Reason = Truncate(record.Reason, 500),
            Error = Truncate(record.Error, 2000)
        });
        await db.SaveChangesAsync(cancellationToken);

        await db.LeadDiscoveryExecutions.IgnoreQueryFilters()
            .Where(e => e.Id == record.ExecutionId && e.TenantId == record.TenantId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.LockStatus, record.To)
                .SetProperty(e => e.LockTokenReference, record.TokenReference)
                .SetProperty(e => e.LockOwnerInstanceId, record.OwnerInstanceId)
                .SetProperty(e => e.LockAcquiredAtUtc, record.AcquiredAtUtc)
                .SetProperty(e => e.LockExpiresAtUtc, record.ExpiresAtUtc)
                .SetProperty(e => e.LockLastRenewedAtUtc, record.LastRenewedAtUtc), cancellationToken);
    }

    /// <summary>The lock row, only while this exact claim holds it with a lease that has not run out.</summary>
    private static IQueryable<LeadDiscoveryLock> OwnedLiveLease(ApplicationDbContext db, LeadDiscoveryLockClaim claim, DateTime now)
    {
        var key = claim.LockKey;
        return db.LeadDiscoveryLocks.Where(l =>
            l.LockKey == key
            && l.ExecutionId == claim.ExecutionId
            && l.LockToken == claim.LockToken
            && l.OwnerInstanceId == claim.OwnerInstanceId
            && l.Status == LeadDiscoveryLockStatus.Acquired
            && l.ExpiresAtUtc > now);
    }

    /// <summary>Why a renew or release matched nothing: our own token still on the row means our lease ran
    /// out (Expired); anything else means someone else owns it, or it is gone (Lost).</summary>
    private static async Task<LeadDiscoveryLockOwnership> ClassifyFailureAsync(
        ApplicationDbContext db, LeadDiscoveryLockClaim claim, CancellationToken cancellationToken)
    {
        var key = claim.LockKey;
        var row = await db.LeadDiscoveryLocks.AsNoTracking()
            .Where(l => l.LockKey == key)
            .Select(l => new { l.LockToken, l.ExecutionId, l.Status })
            .FirstOrDefaultAsync(cancellationToken);

        var stillOurs = row is not null
                        && row.LockToken == claim.LockToken
                        && row.ExecutionId == claim.ExecutionId
                        && row.Status == LeadDiscoveryLockStatus.Acquired;

        return stillOurs ? LeadDiscoveryLockOwnership.Expired : LeadDiscoveryLockOwnership.Lost;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;
}
