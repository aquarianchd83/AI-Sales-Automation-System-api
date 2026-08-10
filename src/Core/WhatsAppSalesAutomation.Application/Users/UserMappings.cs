using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Application.Users;

public static class UserMappings
{
    // IEnumerable rather than IReadOnlyList: UserManager.GetRolesAsync returns IList<string>,
    // which does not implement IReadOnlyList<string>.
    public static UserDto ToDto(this ApplicationUser user, IEnumerable<string> roles) => new(
        user.Id,
        user.FullName,
        user.Email ?? string.Empty,
        user.PhoneNumber,
        user.IsActive,
        roles as IReadOnlyList<string> ?? roles.ToList(),
        user.CreatedAt,
        user.LastLoginAt);
}
