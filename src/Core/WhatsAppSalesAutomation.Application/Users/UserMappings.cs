using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Users;

public static class UserMappings
{
    public static UserDto ToDto(this ApplicationUser user, IReadOnlyList<string> roles) => new(
        user.Id,
        user.FullName,
        user.Email ?? string.Empty,
        user.PhoneNumber,
        user.IsActive,
        roles,
        user.CreatedAt,
        user.LastLoginAt);
}
