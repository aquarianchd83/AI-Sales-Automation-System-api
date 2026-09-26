using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The lease on its own, against an in-memory store: heartbeat renewal and recovery from a failed
/// renewal.</summary>
public sealed class LeadDiscoveryLeaseTests
{
    private readonly TestClock _clock = new();
    private readonly MemoryStore _store = new();

    private LeadDiscoveryLease NewLease(TimeSpan heartbeat) => new(
        _store, _clock, NullLogger.Instance,
        new LeadDiscoveryLeaseSettings(TimeSpan.FromMinutes(10), heartbeat, TimeSpan.FromMinutes(4)),
        new LeadDiscoveryLockClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "instance"),
        new DateTime(2026, 9, 10));

    [Fact]
    public async Task The_heartbeat_renews_in_the_background_and_stops_at_release()
    {
        await using var lease = NewLease(TimeSpan.FromMilliseconds(20));
        Assert.True(await lease.AcquireAsync(CancellationToken.None));

        lease.StartHeartbeat();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_store.Renewals < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        await lease.ReleaseAsync(CancellationToken.None);
        var renewalsAtRelease = _store.Renewals;
        await Task.Delay(100);

        Assert.True(renewalsAtRelease >= 2);
        Assert.Equal(renewalsAtRelease, _store.Renewals);
        Assert.Equal(LeadDiscoveryLockStatus.Released, lease.Status);
    }

    [Fact]
    public async Task A_failed_renewal_blocks_new_work_until_a_retry_succeeds()
    {
        await using var lease = NewLease(TimeSpan.Zero);
        await lease.AcquireAsync(CancellationToken.None);

        _store.FailNextRenewal = true;
        Assert.False(await lease.RenewAsync(CancellationToken.None));
        Assert.Equal(LeadDiscoveryLockStatus.RenewalFailed, lease.Status);

        // The checkpoint retries the renewal before letting work start.
        await lease.EnsureCanProcessAsync(CancellationToken.None);
        Assert.Equal(LeadDiscoveryLockStatus.Acquired, lease.Status);

        var path = _store.Transitions.Select(t => $"{t.From}>{t.To}").ToList();
        Assert.Equal(new[]
        {
            "Pending>Acquiring", "Acquiring>Acquired", "Acquired>Renewing", "Renewing>RenewalFailed",
            "RenewalFailed>Renewing", "Renewing>Acquired"
        }, path);
    }

    [Fact]
    public async Task A_lease_that_elapses_while_renewal_is_failing_expires_and_refuses_work()
    {
        await using var lease = NewLease(TimeSpan.Zero);
        await lease.AcquireAsync(CancellationToken.None);
        _store.FailNextRenewal = true;
        await lease.RenewAsync(CancellationToken.None);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(11);

        var ex = await Assert.ThrowsAsync<LeadDiscoveryLockUnavailableException>(() => lease.EnsureCanProcessAsync(CancellationToken.None));
        Assert.Equal(LeadDiscoveryLockStatus.Expired, ex.Status);

        // Terminal: nothing to release, and no transition out of Expired is attempted.
        await lease.ReleaseAsync(CancellationToken.None);
        Assert.Equal(LeadDiscoveryLockStatus.Expired, _store.Transitions[^1].To);
    }

    private sealed class MemoryStore : ILeadDiscoveryLockStore
    {
        private int _renewals;

        public int Renewals => Volatile.Read(ref _renewals);
        public bool FailNextRenewal { get; set; }
        public List<LeadDiscoveryLockTransitionRecord> Transitions { get; } = new();

        public Task<LeadDiscoveryLockAcquireResult> TryAcquireAsync(LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LeadDiscoveryLockAcquireResult(true, new TestClock().UtcNow + lease, null));

        public Task<LeadDiscoveryLockOperationResult> RenewAsync(LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            if (FailNextRenewal)
            {
                FailNextRenewal = false;
                throw new TimeoutException("database unavailable");
            }

            Interlocked.Increment(ref _renewals);
            return Task.FromResult(new LeadDiscoveryLockOperationResult(LeadDiscoveryLockOwnership.Owned, new TestClock().UtcNow + lease));
        }

        public Task<LeadDiscoveryLockOperationResult> ReleaseAsync(LeadDiscoveryLockClaim claim, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LeadDiscoveryLockOperationResult(LeadDiscoveryLockOwnership.Owned, null));

        public Task RecordTransitionAsync(LeadDiscoveryLockTransitionRecord record, CancellationToken cancellationToken = default)
        {
            lock (Transitions)
                Transitions.Add(record);
            return Task.CompletedTask;
        }
    }
}
