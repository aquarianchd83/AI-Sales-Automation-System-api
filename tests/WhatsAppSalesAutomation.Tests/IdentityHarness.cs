using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>A real Identity UserManager over an in-memory SQLite database, set up like production (same password rules, five-strike
/// lockout, the default token providers) - the account-security rules are Identity's behaviour, so a fake would test nothing.</summary>
public sealed class IdentityHarness : IDisposable
{
    public const string Password = "Old-Passw0rd!";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public SqliteApplicationDbContext Db { get; }

    public UserManager<ApplicationUser> Users { get; }

    public TestClock Clock { get; } = new();

    public IdentityHarness()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        Db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        Db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<ApplicationDbContext>(Db);
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireDigit = true;
                o.User.RequireUniqueEmail = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        _provider = services.BuildServiceProvider();
        Users = _provider.GetRequiredService<UserManager<ApplicationUser>>();
    }

    public async Task<ApplicationUser> AddUserAsync(
        string email = "asha@example.com", string? phone = null, bool phoneConfirmed = false, bool emailConfirmed = true)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, FullName = "Asha", IsActive = true,
            EmailConfirmed = emailConfirmed, PhoneNumber = phone, PhoneNumberConfirmed = phoneConfirmed
        };
        var result = await Users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    public void Dispose()
    {
        Db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }
}
