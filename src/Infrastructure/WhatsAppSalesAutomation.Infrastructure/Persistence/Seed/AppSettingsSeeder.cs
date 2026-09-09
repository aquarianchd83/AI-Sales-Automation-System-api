using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Ensures every AppSettingCatalog key has a row in the AppSettings table, defaulting any that are
/// missing to whatever the merged IConfiguration (i.e. appsettings.json, since the DB provider has
/// nothing for a key that isn't in the DB yet - see AppSettingsConfigurationProvider) currently
/// resolves it to. Run once at startup, after migrations, right before
/// IAppSettingsReloader.Reload() - see Program.cs. Idempotent: only inserts rows that don't already
/// exist, so re-running (or adding a new catalog key in a later release) never overwrites an
/// admin's saved value.
/// </summary>
public static class AppSettingsSeeder
{
    public static async Task SeedDefaultsAsync(IServiceProvider services)
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var dataProtectionProvider = services.GetRequiredService<IDataProtectionProvider>();
        var dateTime = services.GetRequiredService<IDateTimeProvider>();

        var existingKeys = new HashSet<string>(
            await context.AppSettings.Select(s => s.Key).ToListAsync(),
            StringComparer.OrdinalIgnoreCase);

        var missing = AppSettingCatalog.All.Where(d => !existingKeys.Contains(d.Key)).ToList();
        if (missing.Count == 0)
            return;

        var protector = AppSettingsSecretProtection.CreateProtector(dataProtectionProvider);

        foreach (var definition in missing)
        {
            var defaultValue = definition.IsList
                ? string.Join(",", configuration.GetSection(definition.Key).Get<string[]>() ?? [])
                : configuration[definition.Key];

            context.AppSettings.Add(new AppSetting
            {
                Key = definition.Key,
                Value = string.IsNullOrEmpty(defaultValue) ? null : definition.IsSecret ? protector.Protect(defaultValue) : defaultValue,
                IsSecret = definition.IsSecret,
                UpdatedAtUtc = dateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
    }
}
