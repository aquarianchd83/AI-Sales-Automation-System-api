namespace WhatsAppSalesAutomation.Application.Users;

public record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    /// <summary>False until the user follows the link emailed at signup. Only self-signups start false.</summary>
    bool EmailConfirmed = true,
    bool PhoneNumberConfirmed = false);

public record CreateUserRequest(
    string FullName,
    string Email,
    string? PhoneNumber,
    string Password,
    IReadOnlyList<string> Roles);

public record UpdateUserRequest(
    string FullName,
    string? PhoneNumber,
    bool IsActive);

public record AssignRolesRequest(IReadOnlyList<string> Roles);
