using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Seeds a realistic sample data set (sales users, customer tags, customers) so the schema can be
/// explored end to end without hand-typing rows. Deliberately covers the edge cases the schema
/// encodes - every <see cref="OptInStatus"/> value, an inactive user, an unassigned customer, a
/// soft-deleted customer, a customer with no tags - so queries against it exercise real branches.
///
/// Runs only when <c>Seed:DummyData</c> is true, and only on an empty Customers table, so it can
/// never touch a database that already holds real records. Never enable it outside Development.
/// </summary>
public static class DevDataSeeder
{
    private const string DefaultPassword = "DevPass123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue<bool>("Seed:DummyData"))
            return;

        var db = services.GetRequiredService<ApplicationDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        // IgnoreQueryFilters: a soft-deleted row still counts as "already seeded".
        if (await db.Customers.IgnoreQueryFilters().AnyAsync())
            return;

        var password = configuration["Seed:DummyUserPassword"] ?? DefaultPassword;
        var agents = await SeedUsersAsync(userManager, password);
        var tags = await SeedTagsAsync(db);

        await SeedCustomersAsync(db, agents, tags);
    }

    /// <summary>Creates one user per role plus a deactivated one, and returns the sales agents by email.</summary>
    private static async Task<Dictionary<string, ApplicationUser>> SeedUsersAsync(
        UserManager<ApplicationUser> userManager,
        string password)
    {
        var specs = new (string FullName, string Email, string Role, bool IsActive)[]
        {
            ("Priya Sharma", "priya.sharma@example.com", AppRoles.Admin, true),
            ("Marcus Webb", "marcus.webb@example.com", AppRoles.SalesManager, true),
            ("Aisha Khan", "aisha.khan@example.com", AppRoles.SalesAgent, true),
            ("Diego Alvarez", "diego.alvarez@example.com", AppRoles.SalesAgent, true),
            // Deactivated: proves IsActive gates authentication independently of the account existing.
            ("Lena Fischer", "lena.fischer@example.com", AppRoles.SalesAgent, false)
        };

        var created = new Dictionary<string, ApplicationUser>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fullName, email, role, isActive) in specs)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    FullName = fullName,
                    EmailConfirmed = true,
                    IsActive = isActive,
                    CreatedAt = DateTime.UtcNow
                };

                var result = await userManager.CreateAsync(user, password);
                if (!result.Succeeded)
                    continue;

                await userManager.AddToRoleAsync(user, role);
            }

            created[email] = user;
        }

        return created;
    }

    private static async Task<Dictionary<string, CustomerTag>> SeedTagsAsync(ApplicationDbContext db)
    {
        var names = new[] { "vip", "newsletter", "enterprise", "trial", "high-intent", "cold-list", "webinar-2026" };

        var tags = names.ToDictionary(
            name => name,
            name => new CustomerTag { Name = name },
            StringComparer.OrdinalIgnoreCase);

        db.CustomerTags.AddRange(tags.Values);
        await db.SaveChangesAsync();

        return tags;
    }

    private static async Task SeedCustomersAsync(
        ApplicationDbContext db,
        Dictionary<string, ApplicationUser> users,
        Dictionary<string, CustomerTag> tags)
    {
        var now = DateTime.UtcNow;
        var aisha = users.GetValueOrDefault("aisha.khan@example.com")?.Id;
        var diego = users.GetValueOrDefault("diego.alvarez@example.com")?.Id;

        var customers = new List<Customer>
        {
            // --- Opted in: the only customers a campaign may legally message. ---
            new()
            {
                PhoneNumberE164 = "+14155550101",
                FirstName = "Ada",
                LastName = "Lovelace",
                Email = "ada.lovelace@example.com",
                Source = "Website",
                OptInStatus = OptInStatus.OptedIn,
                OptInTimestamp = now.AddDays(-45),
                PreferredLanguage = "en",
                AssignedAgentId = aisha,
                Tags = { tags["vip"], tags["enterprise"], tags["high-intent"] }
            },
            new()
            {
                PhoneNumberE164 = "+442071838750",
                FirstName = "Alan",
                LastName = "Turing",
                Email = "alan.turing@example.com",
                Source = "Import",
                OptInStatus = OptInStatus.OptedIn,
                OptInTimestamp = now.AddDays(-30),
                PreferredLanguage = "en",
                AssignedAgentId = aisha,
                Tags = { tags["vip"], tags["newsletter"] }
            },
            new()
            {
                PhoneNumberE164 = "+919820098200",
                FirstName = "Rohan",
                LastName = "Mehta",
                Email = "rohan.mehta@example.com",
                Source = "Referral",
                OptInStatus = OptInStatus.OptedIn,
                OptInTimestamp = now.AddDays(-12),
                PreferredLanguage = "hi",
                AssignedAgentId = diego,
                Tags = { tags["high-intent"], tags["trial"] }
            },
            new()
            {
                PhoneNumberE164 = "+4915112345678",
                FirstName = "Katharina",
                LastName = "Bauer",
                Email = "katharina.bauer@example.com",
                Source = "Webinar",
                OptInStatus = OptInStatus.OptedIn,
                OptInTimestamp = now.AddDays(-8),
                PreferredLanguage = "de",
                AssignedAgentId = diego,
                Tags = { tags["webinar-2026"], tags["enterprise"] }
            },
            // Opted in but unassigned: shows up in "needs an owner" queues.
            new()
            {
                PhoneNumberE164 = "+5511987654321",
                FirstName = "Beatriz",
                LastName = "Costa",
                Email = "beatriz.costa@example.com",
                Source = "Website",
                OptInStatus = OptInStatus.OptedIn,
                OptInTimestamp = now.AddDays(-3),
                PreferredLanguage = "pt",
                AssignedAgentId = null,
                Tags = { tags["newsletter"] }
            },

            // --- Pending opt-in: imported but not yet consented. Must not be messaged. ---
            new()
            {
                PhoneNumberE164 = "+14155550102",
                FirstName = "Grace",
                LastName = "Hopper",
                Email = "grace.hopper@example.com",
                Source = "Import",
                OptInStatus = OptInStatus.PendingOptIn,
                PreferredLanguage = "en",
                AssignedAgentId = aisha,
                Tags = { tags["cold-list"] }
            },
            new()
            {
                PhoneNumberE164 = "+818012345678",
                FirstName = "Yuki",
                LastName = "Tanaka",
                Source = "Import",
                OptInStatus = OptInStatus.PendingOptIn,
                PreferredLanguage = "ja",
                Tags = { tags["cold-list"] }
            },
            // Minimal row: only the required column. Proves everything else is genuinely optional.
            new()
            {
                PhoneNumberE164 = "+61412345678",
                Source = "Import",
                OptInStatus = OptInStatus.PendingOptIn
            },

            // --- Opted out: STOP received. Permanently excluded from all automation. ---
            new()
            {
                PhoneNumberE164 = "+14155550103",
                FirstName = "Edsger",
                LastName = "Dijkstra",
                Email = "edsger.dijkstra@example.com",
                Source = "Website",
                OptInStatus = OptInStatus.OptedOut,
                OptInTimestamp = now.AddDays(-60),
                OptOutTimestamp = now.AddDays(-20),
                PreferredLanguage = "nl",
                AssignedAgentId = diego,
                Tags = { tags["newsletter"] }
            },
            new()
            {
                PhoneNumberE164 = "+33612345678",
                FirstName = "Camille",
                LastName = "Moreau",
                Source = "Webinar",
                OptInStatus = OptInStatus.OptedOut,
                OptInTimestamp = now.AddDays(-25),
                OptOutTimestamp = now.AddDays(-2),
                PreferredLanguage = "fr",
                Tags = { tags["webinar-2026"] }
            },

            // --- Soft-deleted: invisible to every normal query via the global filter. ---
            new()
            {
                PhoneNumberE164 = "+14155550104",
                FirstName = "Removed",
                LastName = "Duplicate",
                Email = "duplicate@example.com",
                Source = "Import",
                OptInStatus = OptInStatus.OptedIn,
                OptInTimestamp = now.AddDays(-90),
                PreferredLanguage = "en",
                IsDeleted = true,
                DeletedAt = now.AddDays(-5)
            }
        };

        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();
    }
}
