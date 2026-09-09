namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>
/// Lets DI-resolved code (AppSettingsStore after a save, Program.cs after seeding defaults) trigger
/// AppSettingsConfigurationProvider.Reload() without depending on IConfiguration internals directly.
/// Registered as a singleton wrapping the one AppSettingsConfigurationSource instance created in
/// Program.cs before the host is built.
/// </summary>
public interface IAppSettingsReloader
{
    void Reload();
}

public class AppSettingsReloader : IAppSettingsReloader
{
    private readonly AppSettingsConfigurationSource _source;

    public AppSettingsReloader(AppSettingsConfigurationSource source)
    {
        _source = source;
    }

    public void Reload() => _source.Provider?.Reload();
}
