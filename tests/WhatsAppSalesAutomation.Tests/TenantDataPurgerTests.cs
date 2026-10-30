using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The permanent tenant delete: everything the tenant owns goes (found through the EF model, not a hand-kept list), another
/// tenant's rows and the platform audit trail stay.</summary>
public sealed class TenantDataPurgerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;

    public TenantDataPurgerTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Purge_removes_the_tenant_and_its_rows_and_leaves_everything_else()
    {
        var doomed = new Tenant { Name = "Doomed", Slug = "doomed" };
        var kept = new Tenant { Name = "Kept", Slug = "kept" };
        _db.Tenants.AddRange(doomed, kept);
        _db.Customers.AddRange(
            new Customer { TenantId = doomed.Id, PhoneNumberE164 = "+911111111111" },
            new Customer { TenantId = doomed.Id, PhoneNumberE164 = "+912222222222" },
            new Customer { TenantId = kept.Id, PhoneNumberE164 = "+913333333333" });
        _db.Users.AddRange(
            new ApplicationUser { TenantId = doomed.Id, UserName = "a@doomed.test", Email = "a@doomed.test" },
            new ApplicationUser { TenantId = kept.Id, UserName = "a@kept.test", Email = "a@kept.test" });
        _db.PlatformAuditLogEntries.Add(new PlatformAuditLogEntry { ActorEmail = "op@test", Action = "TenantCreated", TargetTenantId = doomed.Id });
        await _db.SaveChangesAsync();

        var result = await new TenantDataPurger(_db).PurgeAsync(doomed.Id);

        Assert.True(result.RowsDeleted >= 4);
        Assert.Null(await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == doomed.Id));
        Assert.Empty(await _db.Customers.IgnoreQueryFilters().Where(c => c.TenantId == doomed.Id).ToListAsync());
        Assert.Empty(await _db.Users.IgnoreQueryFilters().Where(u => u.TenantId == doomed.Id).ToListAsync());

        Assert.NotNull(await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == kept.Id));
        Assert.Single(await _db.Customers.IgnoreQueryFilters().Where(c => c.TenantId == kept.Id).ToListAsync());
        Assert.Single(await _db.Users.IgnoreQueryFilters().Where(u => u.TenantId == kept.Id).ToListAsync());
        Assert.Single(await _db.PlatformAuditLogEntries.Where(a => a.TargetTenantId == doomed.Id).ToListAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
