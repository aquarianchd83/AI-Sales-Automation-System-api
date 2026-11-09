using FluentValidation;
using WhatsAppSalesAutomation.Application.Common;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>An operator setting a tenant user's password (support: the user is locked out and cannot use the emailed link).</summary>
public record ResetUserPasswordRequest(string NewPassword);

public class ResetUserPasswordRequestValidator : AbstractValidator<ResetUserPasswordRequest>
{
    public ResetUserPasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword).MeetsPasswordPolicy();
    }
}

/// <summary>One match for the cross-tenant "which org is this email in" support lookup (spec item #6) -
/// deliberately thin, this is a lookup result, not the full tenant-scoped Users & Roles screen.</summary>
public record PlatformUserSearchResultDto(
    Guid UserId,
    string Email,
    string FullName,
    Guid? TenantId,
    string? TenantName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt);
