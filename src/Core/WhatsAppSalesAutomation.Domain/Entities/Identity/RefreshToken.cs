namespace WhatsAppSalesAutomation.Domain.Entities.Identity;

/// <summary>
/// A rotating JWT refresh token. Only the SHA-256 hash of the raw token is ever persisted
/// (<see cref="TokenHash"/>) - the raw value is returned to the client exactly once at issuance.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

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
