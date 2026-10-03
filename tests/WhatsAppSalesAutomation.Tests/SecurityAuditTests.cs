using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Audit;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// What the tenant's audit trail now records about who can sign in, and about the credentials and rules the workspace runs on. As with
/// the rest of the trail the interesting half is what is NOT recorded: no password hash, no API key, ever - only that one changed.
/// </summary>
public sealed class SecurityAuditTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

    private readonly IdentityHarness _h = new(withAudit: true);

    public void Dispose() => _h.Dispose();

    private List<AuditLog> Logs(string entityName) =>
        _h.Db.AuditLogs.AsNoTracking().IgnoreQueryFilters().Where(a => a.EntityName == entityName).OrderBy(a => a.CreatedAt).ToList();

    /// <summary>The trail is ordered by time, and a test clock that never moves cannot say which of two rows came second.</summary>
    private void Later() => _h.Clock.UtcNow = _h.Clock.UtcNow.AddMinutes(1);

    private async Task<ApplicationUser> UserAsync(Guid? tenantId = null) => await _h.AddUserAsync(tenantId: tenantId ?? Tenant);

    [Fact]
    public async Task Each_wrong_password_is_a_row_with_the_address_it_came_from_and_nobody_as_the_actor()
    {
        var user = await UserAsync();
        _h.Actor.IpAddress = "203.0.113.9";

        await _h.Users.AccessFailedAsync(user);
        Later();
        await _h.Users.AccessFailedAsync(user);

        var failures = Logs("User").Where(a => a.Action == AuditAction.Update && a.ChangesJson.Contains("AccessFailedCount")).ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, a =>
        {
            Assert.Equal(user.Id, a.EntityId);
            Assert.Equal(Tenant, a.TenantId);
            Assert.Null(a.PerformedBy);
            Assert.Equal("203.0.113.9", a.IpAddress);
        });
        Assert.Contains("\"from\":1", failures[1].ChangesJson);
        Assert.Contains("\"to\":2", failures[1].ChangesJson);
    }

    [Fact]
    public async Task A_lockout_is_recorded_when_it_starts()
    {
        var user = await UserAsync();

        for (var i = 0; i < 5; i++)
            await _h.Users.AccessFailedAsync(user);

        Assert.Contains(Logs("User"), a => a.Action == AuditAction.Update && a.ChangesJson.Contains("LockoutEnd"));
    }

    [Fact]
    public async Task A_password_change_is_recorded_as_changed_and_the_hash_is_never_written()
    {
        var user = await UserAsync();
        var hashBefore = user.PasswordHash!;
        _h.Actor.UserId = user.Id;

        var result = await _h.Users.ChangePasswordAsync(user, IdentityHarness.Password, "New-Passw0rd!");
        Assert.True(result.Succeeded);

        var row = Assert.Single(Logs("User"), a => a.ChangesJson.Contains("PasswordHash"));
        Assert.Contains("\"PasswordHash\":\"changed\"", row.ChangesJson);
        Assert.DoesNotContain(hashBefore, row.ChangesJson);
        Assert.DoesNotContain(user.PasswordHash!, row.ChangesJson);
        Assert.Equal(user.Id, row.PerformedBy);
    }

    [Fact]
    public async Task Deactivating_a_user_is_a_status_change()
    {
        var user = await UserAsync();

        user.IsActive = false;
        await _h.Users.UpdateAsync(user);

        var row = Assert.Single(Logs("User"), a => a.Action == AuditAction.StatusChange);
        Assert.Contains("IsActive", row.ChangesJson);
        Assert.Contains("\"from\":true", row.ChangesJson);
        Assert.Contains("\"to\":false", row.ChangesJson);
    }

    [Fact]
    public async Task A_platform_operator_belongs_to_no_tenants_trail()
    {
        var operatorUser = await _h.AddUserAsync("ops@example.com", tenantId: null);
        await _h.Users.AccessFailedAsync(operatorUser);

        Assert.Empty(Logs("User"));
    }

    [Fact]
    public async Task A_new_user_is_recorded_without_a_password()
    {
        var user = await UserAsync();

        var row = Assert.Single(Logs("User"));
        Assert.Equal(AuditAction.Create, row.Action);
        Assert.Equal(user.Id, row.EntityId);
        Assert.DoesNotContain("PasswordHash", row.ChangesJson);
        Assert.DoesNotContain(user.PasswordHash!, row.ChangesJson);
    }

    [Fact]
    public async Task A_changed_whatsapp_token_is_recorded_as_changed_and_never_as_a_value()
    {
        _h.Db.Add(new TenantWhatsAppConfig { TenantId = Tenant, PhoneNumberId = "111", WhatsAppBusinessAccountId = "222", AccessToken = "EAAold-secret-token" });
        await _h.Db.SaveChangesAsync();

        Later();
        var config = await _h.Db.Set<TenantWhatsAppConfig>().IgnoreQueryFilters().SingleAsync();
        config.AccessToken = "EAAnew-secret-token";
        config.PhoneNumberId = "999";
        await _h.Db.SaveChangesAsync();

        var rows = Logs("WhatsAppConfig");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(Tenant, r.EntityId));
        var update = rows[1];
        Assert.Contains("\"AccessToken\":\"changed\"", update.ChangesJson);
        Assert.Contains("\"PhoneNumberId\"", update.ChangesJson);
        Assert.All(rows, r => Assert.DoesNotContain("secret-token", r.ChangesJson));
    }

    [Fact]
    public async Task The_writer_records_what_no_single_row_describes_such_as_a_users_roles()
    {
        var user = await UserAsync();
        _h.Actor.UserId = Guid.NewGuid();
        _h.Actor.IpAddress = "198.51.100.4";
        var writer = new AuditTrailWriter(_h.Db, _h.Actor, _h.Clock);
        Later();

        await writer.RecordAsync(Tenant, "User", user.Id, AuditAction.Update, new Dictionary<string, object?>
        {
            ["Roles"] = new Dictionary<string, object?> { ["from"] = new[] { "SalesAgent" }, ["to"] = new[] { "Admin" } }
        });

        var row = Logs("User").Last();
        Assert.Equal(AuditAction.Update, row.Action);
        Assert.Contains("Admin", row.ChangesJson);
        Assert.Equal(_h.Actor.UserId, row.PerformedBy);
        Assert.Equal("198.51.100.4", row.IpAddress);
        Assert.Equal(Tenant, row.TenantId);
    }
}
