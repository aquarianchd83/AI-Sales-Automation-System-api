using Microsoft.AspNetCore.Identity;

namespace WhatsAppSalesAutomation.Domain.Entities.Identity;

/// <summary>
/// Platform user, backed by ASP.NET Core Identity. Roles (SuperAdmin/Admin/SalesManager/SalesAgent)
/// are managed through Identity's UserRoles join table rather than a custom one.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Soft "disabled" flag. Deactivated users can no longer authenticate.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
