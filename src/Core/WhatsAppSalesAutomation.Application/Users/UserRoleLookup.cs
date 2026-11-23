using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Application.Users;

public static class UserRoleLookup
{
    /// <summary>
    /// Role names for a set of users in one query. Same answer as calling <c>UserManager.GetRolesAsync</c> for each
    /// user, without a round trip per user. A user with no roles is simply absent from the result.
    /// </summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<string>>> RolesByUserAsync(
        this IApplicationDbContext context, IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<string>>();

        var rows = await (
                from ur in context.UserRoles.AsNoTracking()
                join r in context.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where ids.Contains(ur.UserId) && r.Name != null
                select new { ur.UserId, Name = r.Name! })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).ToList());
    }

    public static IReadOnlyList<string> For(this IReadOnlyDictionary<Guid, IReadOnlyList<string>> rolesByUser, Guid userId) =>
        rolesByUser.TryGetValue(userId, out var roles) ? roles : Array.Empty<string>();
}
