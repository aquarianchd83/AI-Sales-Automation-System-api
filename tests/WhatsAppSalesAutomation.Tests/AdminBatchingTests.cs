using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Users;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The admin lists' batched role lookup gives the same answer as Identity's per-user GetRolesAsync, and the
/// page/page-size overload of the short-last-page count matches a real COUNT(*).
/// </summary>
public sealed class AdminBatchingTests : IDisposable
{
    private readonly IdentityHarness _identity = new();

    public void Dispose() => _identity.Dispose();

    private async Task AddRoleAsync(string name)
    {
        _identity.Db.Roles.Add(new ApplicationRole(name) { NormalizedName = name.ToUpperInvariant() });
        await _identity.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Roles_for_a_page_of_users_match_GetRolesAsync()
    {
        await AddRoleAsync(AppRoles.Admin);
        await AddRoleAsync("Agent");

        var both = await _identity.AddUserAsync("both@example.com");
        var agent = await _identity.AddUserAsync("agent@example.com");
        var none = await _identity.AddUserAsync("none@example.com");
        Assert.True((await _identity.Users.AddToRolesAsync(both, new[] { AppRoles.Admin, "Agent" })).Succeeded);
        Assert.True((await _identity.Users.AddToRoleAsync(agent, "Agent")).Succeeded);

        var rolesByUser = await _identity.Db.RolesByUserAsync(new[] { both.Id, agent.Id, none.Id });

        foreach (var user in new[] { both, agent, none })
        {
            var expected = (await _identity.Users.GetRolesAsync(user)).OrderBy(r => r).ToList();
            Assert.Equal(expected, rolesByUser.For(user.Id).OrderBy(r => r).ToList());
        }
    }

    [Fact]
    public async Task No_users_means_no_query_and_no_roles()
    {
        var rolesByUser = await _identity.Db.RolesByUserAsync(Array.Empty<Guid>());
        Assert.Empty(rolesByUser);
    }

    [Theory]
    [InlineData(1, 10)] // short first page
    [InlineData(2, 4)]  // short last page
    [InlineData(1, 4)]  // full page: counted
    [InlineData(1, 7)]  // exactly full: counted
    [InlineData(3, 4)]  // past the end: counted
    public async Task The_page_size_overload_totals_match_a_real_count(int page, int pageSize)
    {
        for (var i = 0; i < 7; i++)
            await AddRoleAsync($"Role{i}");

        var roles = _identity.Db.Roles.AsNoTracking();
        var rows = await roles.OrderBy(r => r.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        Assert.Equal(7, await roles.CountForPageAsync(page, pageSize, rows.Count));
    }
}
