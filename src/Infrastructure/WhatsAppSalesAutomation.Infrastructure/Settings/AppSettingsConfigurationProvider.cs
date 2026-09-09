using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>
/// Feeds the AppSettings table into IConfiguration, added to the host's configuration pipeline
/// after the JSON files (see Program.cs) so a DB row always wins over its appsettings.json
/// counterpart. Runs via raw ADO.NET rather than EF Core / ApplicationDbContext because <see
/// cref="Load"/> executes synchronously while WebApplicationBuilder.Configuration is still being
/// assembled - well before the DI container (and its DbContext) exists.
/// </summary>
public class AppSettingsConfigurationProvider : ConfigurationProvider
{
    private readonly Func<string> _connectionStringAccessor;
    private readonly string _contentRootPath;

    public AppSettingsConfigurationProvider(Func<string> connectionStringAccessor, string contentRootPath)
    {
        _connectionStringAccessor = connectionStringAccessor;
        _contentRootPath = contentRootPath;
    }

    public override void Load() => Data = FetchData();

    /// <summary>Re-reads the table and fires IConfiguration's change token, which is what makes
    /// every IOptionsSnapshot&lt;T&gt;/IOptionsMonitor&lt;T&gt; consumer pick up a saved change with
    /// no app restart. Called by AppSettingsReloader right after Program.cs seeds/updates rows.</summary>
    public void Reload()
    {
        Data = FetchData();
        OnReload();
    }

    private IDictionary<string, string?> FetchData()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = new SqlConnection(_connectionStringAccessor());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT [Key], [Value], [IsSecret] FROM [AppSettings]";
            using var reader = command.ExecuteReader();

            IDataProtector? protector = null;

            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (reader.IsDBNull(1))
                    continue;

                var value = reader.GetString(1);
                var isSecret = reader.GetBoolean(2);

                if (isSecret)
                {
                    protector ??= AppSettingsSecretProtection.CreateStandaloneProtector(_contentRootPath);
                    try
                    {
                        value = protector.Unprotect(value);
                    }
                    catch (Exception ex)
                    {
                        // Ciphertext from a different/rotated key ring - skip rather than feed garbage
                        // into IConfiguration; the appsettings.json fallback (if any) still applies.
                        LogFallback($"AppSettings row '{key}' could not be decrypted - ignoring stored value", ex);
                        continue;
                    }
                }

                var definition = AppSettingCatalog.All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
                if (definition is { IsList: true })
                {
                    var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    for (var i = 0; i < items.Length; i++)
                        data[$"{key}:{i}"] = items[i];
                }
                else
                {
                    data[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            // Most commonly: the AppSettings table doesn't exist yet (fresh DB, migrations haven't
            // run) or the DB is briefly unreachable at boot. Falling back to whatever
            // appsettings.json already has for these keys keeps host startup from failing outright;
            // AppSettingsSeeder + a Reload() call right after migrations run (see Program.cs) pick
            // the DB-backed values back up before the app serves its first request.
            LogFallback("Could not load AppSettings table - falling back to appsettings.json values for this boot", ex);
        }

        return data;
    }

    // No ILogger available - this runs before the DI container exists (see the class doc comment).
    // Serilog's bootstrap Log.Logger isn't referenced here to avoid a new package dependency on this
    // Infrastructure project just for two rare, non-fatal warnings; stderr is enough to surface them
    // during startup diagnosis without pretending this is wired into the app's real log pipeline.
    private static void LogFallback(string message, Exception ex)
        => Console.Error.WriteLine($"[AppSettingsConfigurationProvider] {message}: {ex.Message}");
}

public class AppSettingsConfigurationSource : IConfigurationSource
{
    private readonly Func<string> _connectionStringAccessor;
    private readonly string _contentRootPath;

    public AppSettingsConfigurationSource(Func<string> connectionStringAccessor, string contentRootPath)
    {
        _connectionStringAccessor = connectionStringAccessor;
        _contentRootPath = contentRootPath;
    }

    /// <summary>Set once Build() runs - AppSettingsReloader holds onto this to call Reload() later.</summary>
    public AppSettingsConfigurationProvider? Provider { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        Provider = new AppSettingsConfigurationProvider(_connectionStringAccessor, _contentRootPath);
        return Provider;
    }
}
