namespace WhatsAppSalesAutomation.Infrastructure.Settings;

/// <summary>
/// One DB-backed override of an appsettings.json key - see AppSettingCatalog (Application layer)
/// for which keys this app allows here. <see cref="Key"/> mirrors IConfiguration's own ":"-separated
/// section path (e.g. "AiProviders:OpenAI:ApiKey") and doubles as the table's primary key, so there
/// is exactly one row per catalog entry. Not exposed via IApplicationDbContext - same
/// Infrastructure-internal reasoning as WhatsAppAccessTokenState; Application reaches this only
/// through IAppSettingsStore/ISettingsService, never the entity itself.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;

    /// <summary>Plain text for non-secret keys. For AppSettingCatalog IsSecret keys, this is
    /// ciphertext produced by AppSettingsSecretProtection - never written or read unencrypted.</summary>
    public string? Value { get; set; }

    public bool IsSecret { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
