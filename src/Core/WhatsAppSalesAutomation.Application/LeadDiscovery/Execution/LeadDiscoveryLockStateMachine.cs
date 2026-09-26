using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;

/// <summary>
/// The only definition of which lock-state transitions are legal. Anything not listed here - Blocked to
/// anything, a terminal state back to Acquired, Renewing straight to ReleasePending - is rejected.
/// </summary>
public static class LeadDiscoveryLockStateMachine
{
    private static readonly IReadOnlyDictionary<LeadDiscoveryLockStatus, LeadDiscoveryLockStatus[]> Allowed =
        new Dictionary<LeadDiscoveryLockStatus, LeadDiscoveryLockStatus[]>
        {
            [LeadDiscoveryLockStatus.Pending] = new[] { LeadDiscoveryLockStatus.Acquiring },
            [LeadDiscoveryLockStatus.Acquiring] = new[] { LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStatus.Blocked },
            [LeadDiscoveryLockStatus.Acquired] = new[]
            {
                LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.ReleasePending,
                LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.Lost
            },
            [LeadDiscoveryLockStatus.Renewing] = new[]
            {
                LeadDiscoveryLockStatus.Acquired, LeadDiscoveryLockStatus.RenewalFailed,
                LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.Lost
            },
            [LeadDiscoveryLockStatus.RenewalFailed] = new[]
            {
                LeadDiscoveryLockStatus.Renewing, LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.Lost
            },
            // A failed release is retried while the lease is still valid (ReleasePending -> ReleasePending).
            [LeadDiscoveryLockStatus.ReleasePending] = new[]
            {
                LeadDiscoveryLockStatus.ReleasePending, LeadDiscoveryLockStatus.Released,
                LeadDiscoveryLockStatus.Expired, LeadDiscoveryLockStatus.Lost
            },
            [LeadDiscoveryLockStatus.Released] = Array.Empty<LeadDiscoveryLockStatus>(),
            [LeadDiscoveryLockStatus.Blocked] = Array.Empty<LeadDiscoveryLockStatus>(),
            [LeadDiscoveryLockStatus.Expired] = Array.Empty<LeadDiscoveryLockStatus>(),
            [LeadDiscoveryLockStatus.Lost] = Array.Empty<LeadDiscoveryLockStatus>()
        };

    public static bool CanTransition(LeadDiscoveryLockStatus from, LeadDiscoveryLockStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Blocked, Released, Expired and Lost: the execution can never process again. Continuing needs a
    /// new execution with a new token.</summary>
    public static bool IsTerminal(LeadDiscoveryLockStatus status) =>
        status is LeadDiscoveryLockStatus.Blocked or LeadDiscoveryLockStatus.Released
            or LeadDiscoveryLockStatus.Expired or LeadDiscoveryLockStatus.Lost;

    /// <summary>The core locking rule: only Acquired may start new business processing.</summary>
    public static bool AllowsBusinessProcessing(LeadDiscoveryLockStatus status) =>
        status == LeadDiscoveryLockStatus.Acquired;
}

/// <summary>Thrown for a transition the state machine does not allow. Always a bug, never a runtime condition.</summary>
public sealed class InvalidLockTransitionException : InvalidOperationException
{
    public InvalidLockTransitionException(LeadDiscoveryLockStatus from, LeadDiscoveryLockStatus to)
        : base($"Invalid lead discovery lock transition {from} -> {to}.")
    {
        From = from;
        To = to;
    }

    public LeadDiscoveryLockStatus From { get; }
    public LeadDiscoveryLockStatus To { get; }
}

/// <summary>Thrown at a processing checkpoint when the execution no longer holds its lock in Acquired state -
/// it must stop starting new work. Already committed work stays committed.</summary>
public sealed class LeadDiscoveryLockUnavailableException : Exception
{
    public LeadDiscoveryLockUnavailableException(LeadDiscoveryLockStatus status, string reason)
        : base($"The lead discovery lock is {status}: {reason}")
    {
        Status = status;
    }

    public LeadDiscoveryLockStatus Status { get; }
}
