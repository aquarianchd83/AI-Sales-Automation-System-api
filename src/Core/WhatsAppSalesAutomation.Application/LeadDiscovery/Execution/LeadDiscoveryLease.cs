using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;

/// <summary>Timings for <see cref="LeadDiscoveryLease"/>.</summary>
/// <param name="Duration">How long one acquisition or renewal is valid for.</param>
/// <param name="HeartbeatInterval">How often the background heartbeat renews. Zero disables the heartbeat,
/// leaving renewal to the processing checkpoints alone.</param>
/// <param name="RenewWhenRemaining">A checkpoint renews inline when less than this is left on the lease.</param>
public sealed record LeadDiscoveryLeaseSettings(TimeSpan Duration, TimeSpan HeartbeatInterval, TimeSpan RenewWhenRemaining);

/// <summary>
/// One execution's hold on the lead-discovery lock, and the only thing that moves its lock state. Every
/// transition is checked against <see cref="LeadDiscoveryLockStateMachine"/> and recorded to history.
///
/// Renewal happens two ways: a background heartbeat for long operations (an agent round can take minutes),
/// and inline at every processing checkpoint when the lease is running low. Both run under one gate, so a
/// checkpoint that arrives while a renewal is in flight waits for it - no new business work starts in
/// Renewing or RenewalFailed.
/// </summary>
public sealed class LeadDiscoveryLease : IAsyncDisposable
{
    private const int ReleaseAttempts = 3;

    private readonly ILeadDiscoveryLockStore _store;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger _logger;
    private readonly LeadDiscoveryLeaseSettings _settings;
    private readonly DateTime _processingDate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeat;
    private DateTime? _acquiredAtUtc;
    private DateTime? _expiresAtUtc;
    private DateTime? _lastRenewedAtUtc;

    public LeadDiscoveryLease(
        ILeadDiscoveryLockStore store,
        IDateTimeProvider clock,
        ILogger logger,
        LeadDiscoveryLeaseSettings settings,
        LeadDiscoveryLockClaim claim,
        DateTime processingDate)
    {
        _store = store;
        _clock = clock;
        _logger = logger;
        _settings = settings;
        Claim = claim;
        _processingDate = processingDate;
    }

    public LeadDiscoveryLockClaim Claim { get; }

    public LeadDiscoveryLockStatus Status { get; private set; } = LeadDiscoveryLockStatus.Pending;

    /// <summary>Set when acquisition was blocked: the execution that holds the lock.</summary>
    public Guid? BlockedByExecutionId { get; private set; }

    /// <summary>Pending -> Acquiring -> Acquired, or -> Blocked when another execution owns the lock. Never
    /// waits for the lock: a scheduled run that finds it held skips and exits.</summary>
    public async Task<bool> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await TransitionAsync(LeadDiscoveryLockStatus.Acquiring, "Attempting to acquire the lock", null, cancellationToken);

            LeadDiscoveryLockAcquireResult result;
            try
            {
                result = await _store.TryAcquireAsync(Claim, _settings.Duration, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Lead discovery lock acquisition failed for {LockKey}", Claim.LockKey);
                await TransitionAsync(LeadDiscoveryLockStatus.Blocked, "Lock acquisition failed", ex.Message, cancellationToken);
                return false;
            }

            if (!result.Acquired)
            {
                BlockedByExecutionId = result.HeldByExecutionId;
                await TransitionAsync(LeadDiscoveryLockStatus.Blocked,
                    result.HeldByExecutionId is { } holder
                        ? $"Lock is held by execution {holder}"
                        : "Lock is held by another execution",
                    null, cancellationToken);
                return false;
            }

            _acquiredAtUtc = _clock.UtcNow;
            _expiresAtUtc = result.ExpiresAtUtc;
            await TransitionAsync(LeadDiscoveryLockStatus.Acquired, "Lock acquired", null, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts the background heartbeat. Idempotent; a no-op when the heartbeat is disabled.</summary>
    public void StartHeartbeat()
    {
        if (_heartbeat is not null || _settings.HeartbeatInterval <= TimeSpan.Zero)
            return;

        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;
        _heartbeat = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_settings.HeartbeatInterval, token);
                    await _gate.WaitAsync(token);
                    try
                    {
                        if (LeadDiscoveryLockStateMachine.IsTerminal(Status))
                            return;
                        if (Status is LeadDiscoveryLockStatus.Acquired or LeadDiscoveryLockStatus.RenewalFailed)
                            await RenewCoreAsync("Heartbeat", token);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lead discovery lock heartbeat failed for {LockKey}", Claim.LockKey);
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// The processing checkpoint - call before starting any new unit of business work. Renews inline when the
    /// lease is running low or a renewal previously failed, and throws
    /// <see cref="LeadDiscoveryLockUnavailableException"/> unless the lock ends up Acquired with a valid lease.
    /// </summary>
    public async Task EnsureCanProcessAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status == LeadDiscoveryLockStatus.RenewalFailed)
                await RenewCoreAsync("Retrying a failed renewal before new work", cancellationToken);
            else if (Status == LeadDiscoveryLockStatus.Acquired && Remaining() <= _settings.RenewWhenRemaining)
                await RenewCoreAsync("Lease running low", cancellationToken);

            if (Status == LeadDiscoveryLockStatus.Acquired && Remaining() <= TimeSpan.Zero)
                await TransitionAsync(LeadDiscoveryLockStatus.Expired, "Lease elapsed without a successful renewal", null, cancellationToken);

            if (!LeadDiscoveryLockStateMachine.AllowsBusinessProcessing(Status))
                throw new LeadDiscoveryLockUnavailableException(Status, "no new lead discovery work may start");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Renews now, outside the heartbeat schedule. Returns whether the lock is Acquired afterwards.</summary>
    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status is LeadDiscoveryLockStatus.Acquired or LeadDiscoveryLockStatus.RenewalFailed)
                await RenewCoreAsync("Explicit renewal", cancellationToken);
            return Status == LeadDiscoveryLockStatus.Acquired;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Acquired -> ReleasePending -> Released. A failed release is retried while the lease is still valid; one
    /// whose lease elapses first ends Expired, and one the store reports as taken ends Lost. Does nothing for
    /// an execution that never acquired (Blocked has nothing to release) or already lost the lock.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await StopHeartbeatAsync();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status == LeadDiscoveryLockStatus.RenewalFailed)
                await RenewCoreAsync("Renewing before release", cancellationToken);

            if (Status != LeadDiscoveryLockStatus.Acquired)
                return;

            await TransitionAsync(LeadDiscoveryLockStatus.ReleasePending, "Processing finished; releasing the lock", null, cancellationToken);

            for (var attempt = 1; attempt <= ReleaseAttempts; attempt++)
            {
                if (Remaining() <= TimeSpan.Zero)
                {
                    await TransitionAsync(LeadDiscoveryLockStatus.Expired, "Lease elapsed before the release succeeded", null, cancellationToken);
                    return;
                }

                LeadDiscoveryLockOperationResult result;
                try
                {
                    result = await _store.ReleaseAsync(Claim, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Lead discovery lock release attempt {Attempt} failed for {LockKey}", attempt, Claim.LockKey);
                    await TransitionAsync(LeadDiscoveryLockStatus.ReleasePending, $"Release attempt {attempt} failed", ex.Message, cancellationToken);
                    continue;
                }

                switch (result.Ownership)
                {
                    case LeadDiscoveryLockOwnership.Owned:
                        await TransitionAsync(LeadDiscoveryLockStatus.Released, "Lock released", null, cancellationToken);
                        return;
                    case LeadDiscoveryLockOwnership.Expired:
                        await TransitionAsync(LeadDiscoveryLockStatus.Expired, "Lease had elapsed at release", null, cancellationToken);
                        return;
                    default:
                        await TransitionAsync(LeadDiscoveryLockStatus.Lost, "Lock was owned by another execution at release", null, cancellationToken);
                        return;
                }
            }

            // Still ReleasePending: the lease will simply run out, after which the next execution can acquire.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopHeartbeatAsync();
        _gate.Dispose();
    }

    private async Task StopHeartbeatAsync()
    {
        if (_heartbeatCts is null || _heartbeat is null)
            return;

        _heartbeatCts.Cancel();
        try
        {
            await _heartbeat;
        }
        catch (OperationCanceledException)
        {
        }

        _heartbeatCts.Dispose();
        _heartbeatCts = null;
        _heartbeat = null;
    }

    private TimeSpan Remaining() => (_expiresAtUtc ?? DateTime.MinValue) - _clock.UtcNow;

    /// <summary>Acquired/RenewalFailed -> Renewing -> Acquired | RenewalFailed | Expired | Lost. Must be called
    /// holding the gate.</summary>
    private async Task RenewCoreAsync(string reason, CancellationToken cancellationToken)
    {
        if (Remaining() <= TimeSpan.Zero)
        {
            await TransitionAsync(LeadDiscoveryLockStatus.Expired, "Lease elapsed before it could be renewed", null, cancellationToken);
            return;
        }

        await TransitionAsync(LeadDiscoveryLockStatus.Renewing, reason, null, cancellationToken);

        LeadDiscoveryLockOperationResult result;
        try
        {
            result = await _store.RenewAsync(Claim, _settings.Duration, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Lead discovery lock renewal failed for {LockKey}", Claim.LockKey);
            if (Remaining() <= TimeSpan.Zero)
                await TransitionAsync(LeadDiscoveryLockStatus.Expired, "Lease elapsed while renewal was failing", ex.Message, cancellationToken);
            else
                await TransitionAsync(LeadDiscoveryLockStatus.RenewalFailed, "Renewal failed", ex.Message, cancellationToken);
            return;
        }

        switch (result.Ownership)
        {
            case LeadDiscoveryLockOwnership.Owned:
                _expiresAtUtc = result.ExpiresAtUtc;
                _lastRenewedAtUtc = _clock.UtcNow;
                await TransitionAsync(LeadDiscoveryLockStatus.Acquired, "Lease renewed", null, cancellationToken);
                break;
            case LeadDiscoveryLockOwnership.Expired:
                await TransitionAsync(LeadDiscoveryLockStatus.Expired, "Lease had elapsed at renewal", null, cancellationToken);
                break;
            default:
                await TransitionAsync(LeadDiscoveryLockStatus.Lost, "Lock provider reports another owner", null, cancellationToken);
                break;
        }
    }

    /// <summary>Validates and applies one transition, then records it. A transition the state machine rejects
    /// is logged and thrown - it is a bug. A failure to record is logged but does not undo the transition: the
    /// in-memory state is what guards processing.</summary>
    private async Task TransitionAsync(LeadDiscoveryLockStatus to, string reason, string? error, CancellationToken cancellationToken)
    {
        var from = Status;
        if (!LeadDiscoveryLockStateMachine.CanTransition(from, to))
        {
            _logger.LogError("Rejected invalid lead discovery lock transition {From} -> {To} for execution {ExecutionId}",
                from, to, Claim.ExecutionId);
            throw new InvalidLockTransitionException(from, to);
        }

        Status = to;

        try
        {
            await _store.RecordTransitionAsync(new LeadDiscoveryLockTransitionRecord(
                Claim.TenantId, Claim.ProfileId, Claim.ExecutionId, Claim.TokenReference, Claim.OwnerInstanceId,
                _processingDate, from, to, _clock.UtcNow, reason, error,
                _acquiredAtUtc, _expiresAtUtc, _lastRenewedAtUtc), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record lead discovery lock transition {From} -> {To} for execution {ExecutionId}",
                from, to, Claim.ExecutionId);
        }
    }
}
