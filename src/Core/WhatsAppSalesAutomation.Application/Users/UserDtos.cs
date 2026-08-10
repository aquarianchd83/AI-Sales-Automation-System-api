namespace WhatsAppSalesAutomation.Application.Users;

public record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

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
