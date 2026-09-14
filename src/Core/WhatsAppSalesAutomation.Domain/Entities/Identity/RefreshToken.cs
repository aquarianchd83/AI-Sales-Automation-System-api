namespace WhatsAppSalesAutomation.Domain.Entities.Identity;

/// <summary>
/// A rotating JWT refresh token. Only the SHA-256 hash of the raw token is ever persisted
/// (<see cref="TokenHash"/>) - the raw value is returned to the client exactly once at issuance.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Snapshot of the owning user's TenantId at issuance time (null for a PlatformSuperAdmin).
    /// Deliberately nullable and NOT <see cref="Common.ITenantOwned"/>, unlike every other tenant-owned
    /// entity: a refresh token is issued from <c>AuthService.LoginAsync</c> before the request that
    /// issues it has any ambient tenant in scope (login itself is unauthenticated), so there is no
    /// <c>ITenantContext</c> value for the stamping interceptor to pull from - the caller sets this
    /// explicitly instead (see <c>AuthService.IssueTokenPairAsync</c>). Rows are only ever reached by
    /// exact <see cref="TokenHash"/> match or by <see cref="UserId"/> (already itself tenant-correct,
    /// since a user belongs to exactly one tenant) - never listed/browsed - so skipping the global
    /// query filter here does not weaken isolation in practice.
    /// </summary>
    public Guid? TenantId { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAt { get; set; }

    public string? CreatedByIp { get; set; }

    /// <summary>Hash of the token this one was rotated into, set when a refresh is used. Aids reuse/theft detection.</summary>
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
}
