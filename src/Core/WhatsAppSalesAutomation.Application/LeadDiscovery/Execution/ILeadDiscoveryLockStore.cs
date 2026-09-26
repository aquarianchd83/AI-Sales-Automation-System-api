using System.Security.Cryptography;
using System.Text;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.LeadDiscovery.Execution;

/// <summary>Who is claiming a lock: every operation after acquisition must match all of these.</summary>
public sealed record LeadDiscoveryLockClaim(
    Guid TenantId,
    Guid ProfileId,
    Guid ExecutionId,
    Guid LockToken,
    string OwnerInstanceId)
{
    /// <summary>Tenant + profile: different tenants and different profiles never contend.</summary>
    public string LockKey => KeyFor(TenantId, ProfileId);

    /// <summary>A non-secret reference to the token for history - a hash prefix, never the token itself.</summary>
    public string TokenReference => ReferenceFor(LockToken);

    public static string KeyFor(Guid tenantId, Guid profileId) => $"lead-discovery:{tenantId:N}:{profileId:N}";

    public static string ReferenceFor(Guid token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.ToString("N"))))[..12].ToLowerInvariant();
}

public sealed record LeadDiscoveryLockAcquireResult(bool Acquired, DateTime? ExpiresAtUtc, Guid? HeldByExecutionId);

public enum LeadDiscoveryLockOwnership
{
    /// <summary>The operation succeeded: the claim owned a live lease.</summary>
    Owned,

    /// <summary>The claim's own lease had already elapsed.</summary>
    Expired,

    /// <summary>The lease now belongs to someone else, or no longer exists.</summary>
    Lost
}

public sealed record LeadDiscoveryLockOperationResult(LeadDiscoveryLockOwnership Ownership, DateTime? ExpiresAtUtc);

/// <summary>One lock transition to record. Carries the token reference rather than a claim, because a stale
/// execution found by its successor is recorded as Expired without its token ever being known again.</summary>
public sealed record LeadDiscoveryLockTransitionRecord(
    Guid TenantId,
    Guid ProfileId,
    Guid ExecutionId,
    string? TokenReference,
    string? OwnerInstanceId,
    DateTime ProcessingDate,
    LeadDiscoveryLockStatus From,
    LeadDiscoveryLockStatus To,
    DateTime AtUtc,
    string? Reason,
    string? Error,
    DateTime? AcquiredAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? LastRenewedAtUtc);

/// <summary>
/// The shared, cross-instance lease store behind the lead-discovery lock. An in-memory lock would only
/// protect one process; this is backed by the database every instance shares, and every operation is an
/// atomic compare-and-set on the lock row.
///
/// Implementations do their own persistence in their own short-lived scopes, so they are safe to call from
/// the heartbeat thread while the orchestrator is using its DbContext.
/// </summary>
public interface ILeadDiscoveryLockStore
{
    /// <summary>Takes the lease when it is free, released, or expired; otherwise reports who holds it.</summary>
    Task<LeadDiscoveryLockAcquireResult> TryAcquireAsync(LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Extends the lease - only for the exact claim that holds a still-valid lease.</summary>
    Task<LeadDiscoveryLockOperationResult> RenewAsync(LeadDiscoveryLockClaim claim, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Releases the lease - only for the exact claim that holds a still-valid lease. Never releases with
    /// an expired or foreign token.</summary>
    Task<LeadDiscoveryLockOperationResult> ReleaseAsync(LeadDiscoveryLockClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Appends the transition to Lead Discovery History and mirrors the new lock state onto the
    /// execution row.</summary>
    Task RecordTransitionAsync(LeadDiscoveryLockTransitionRecord record, CancellationToken cancellationToken = default);
}
