using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Seeds the fixed roles (the four tenant-scoped ones plus PlatformSuperAdmin) and, if configured, a
/// first PlatformSuperAdmin so there is always a way to log in on a fresh database. Run once at
/// startup from Program.cs.
///
/// Before multi-tenancy, <c>Seed:SuperAdminEmail</c>/<c>SuperAdminPassword</c> seeded a tenant-scoped
/// SuperAdmin; there is no tenant to attach one to at this point in startup, so this now seeds the
/// platform-operator account instead - a tenant's own first Admin comes from self-serve signup
/// (<c>AuthService.SignUpAsync</c>) instead, same as any real customer would create one.
/// </summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var configuration = services.GetRequiredService<IConfiguration>();

        foreach (var roleName in AppRoles.All.Append(AppRoles.PlatformSuperAdmin))
        {
            if (!await roleManager.RoleExistsAsync(roleName))
                await roleManager.CreateAsync(new ApplicationRole(roleName));
        }

        var adminEmail = configuration["Seed:SuperAdminEmail"];
        var adminPassword = configuration["Seed:SuperAdminPassword"];

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            return;

        var existing = await userManager.FindByEmailAsync(adminEmail);
        if (existing is not null)
            return;

        var admin = new ApplicationUser
        {
            TenantId = null,
            UserName = adminEmail,
            Email = adminEmail,
            FullName = "Platform Super Admin",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(admin, adminPassword);
        if (result.Succeeded)
            await userManager.AddToRoleAsync(admin, AppRoles.PlatformSuperAdmin);
    }
}
