using Microsoft.AspNetCore.DataProtection;

namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>
/// Shared Data Protection setup for encrypting AppSettingCatalog IsSecret values at rest, used two
/// ways: through DI (AppSettingsStore, via the app's own IDataProtectionProvider registered in
/// Program.cs) and standalone (AppSettingsConfigurationProvider, which runs before the DI container
/// exists - see its own doc comment). Both call <see cref="CreateProtector"/> with the same purpose
/// string against the same on-disk key ring, so either one can decrypt what the other encrypted.
/// Keys live under App_Data/keys - local disk, same convention as LocalFileMediaStorageService's
/// App_Data/media - so the encryption key itself never needs to live in appsettings.json, source
/// control, or a cloud secret store.
/// </summary>
public static class AppSettingsSecretProtection
{
    private const string Purpose = "WhatsAppSalesAutomation.AppSettings.Secrets.v1";

    /// <summary>Shared with Program.cs's own AddDataProtection().SetApplicationName(...) call, so
    /// the DI-registered provider and this class's standalone one derive keys the same way.</summary>
    public const string ApplicationName = "WhatsAppSalesAutomation";

    public static string KeyRingPath(string contentRootPath) => Path.Combine(contentRootPath, "App_Data", "keys");

    /// <summary>For use inside DI, where an IDataProtectionProvider is already registered against
    /// the same key ring (see Program.cs's AddDataProtection().PersistKeysToFileSystem(...) call).</summary>
    public static IDataProtector CreateProtector(IDataProtectionProvider provider) => provider.CreateProtector(Purpose);

    /// <summary>For use outside DI (AppSettingsConfigurationProvider.Load(), which runs while the
    /// host is still being built). Builds a standalone provider pointed at the same key ring path
    /// and application name so it decrypts exactly what the DI-registered provider encrypted.</summary>
    public static IDataProtector CreateStandaloneProtector(string contentRootPath)
    {
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(KeyRingPath(contentRootPath)),
            options => options.SetApplicationName(ApplicationName));

        return provider.CreateProtector(Purpose);
    }
}
