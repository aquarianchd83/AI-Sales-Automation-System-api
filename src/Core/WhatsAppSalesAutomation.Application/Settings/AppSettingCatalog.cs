namespace WhatsAppSalesAutomation.Application.Settings;

/// <summary>
/// One config key this app allows to be moved from appsettings.json into the DB-backed AppSettings
/// table and edited from the admin UI. <see cref="Key"/> uses the same ":" section separators
/// IConfiguration itself uses (e.g. "AiProviders:OpenAI:ApiKey"), which is also the primary key of
/// the AppSettings table row.
/// </summary>
/// <param name="IsSecret">Encrypted at rest and never returned in full by the settings API - see
/// SettingsService.</param>
/// <param name="IsList">Bound to a comma-separated string in the DB/UI (e.g. "1,5,15,60,240") but
/// expanded into IConfiguration's own indexed "Key:0", "Key:1", ... shape so the existing
/// List&lt;T&gt;/array-typed Options properties keep binding the same way they do from JSON today.</param>
/// <param name="IsTenantOverridable">True for the subset of keys a PlatformSuperAdmin can also override
/// per-tenant from a tenant's detail page (see ITenantConfigOverrideProvider) - business tuning knobs
/// (Campaigns/Media/Messaging/Ai) where different tenants reasonably want different values. False for
/// everything else here: WhatsApp/AiProviders already have their own bespoke per-tenant mechanism
/// (TenantWhatsAppConfig/TenantAiProviderConfig), and MediaStorage is platform infra shared by every
/// tenant (one file store) - it doesn't belong in a per-tenant override table either.</param>
public record AppSettingDefinition(string Key, string Category, bool IsSecret, bool IsList = false, string? Description = null, bool IsTenantOverridable = false);

/// <summary>
/// Single source of truth for which appsettings.json sections (<see cref="Categories"/>) are
/// DB-backed and UI-editable - see the "Move config into DB" plan. Everything else
/// (ConnectionStrings, Serilog, Jwt, LogViewer, Seed, AllowedHosts) is intentionally left out and
/// keeps reading straight from appsettings.json - none of it is something an admin would ever want
/// to change without a deploy alongside it.
/// </summary>
public static class AppSettingCatalog
{
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "WhatsApp", "AiProviders", "Campaigns", "Media", "Messaging", "Ai", "MediaStorage"
    };

    public static readonly IReadOnlyList<AppSettingDefinition> All = new List<AppSettingDefinition>
    {
        // WhatsApp - Provider stays restart-required (picks the concrete IWhatsAppService at DI-build
        // time), everything else here is live with no restart once saved.
        new("WhatsApp:Provider", "WhatsApp", IsSecret: false, Description: "\"Simulated\" or \"Meta\" - restart required to take effect."),
        new("WhatsApp:PhoneNumberId", "WhatsApp", IsSecret: false),
        new("WhatsApp:WhatsAppBusinessAccountId", "WhatsApp", IsSecret: false),
        new("WhatsApp:AccessToken", "WhatsApp", IsSecret: true),
        new("WhatsApp:ApiVersion", "WhatsApp", IsSecret: false),
        new("WhatsApp:ApiBaseUrl", "WhatsApp", IsSecret: false),
        new("WhatsApp:SimulatedFailureRatePercent", "WhatsApp", IsSecret: false),
        new("WhatsApp:AppSecret", "WhatsApp", IsSecret: true),
        new("WhatsApp:AppId", "WhatsApp", IsSecret: false),
        new("WhatsApp:WebhookVerifyToken", "WhatsApp", IsSecret: true),

        // AiProviders - Provider/EmbeddingProvider stay restart-required, same reasoning as above.
        new("AiProviders:Provider", "AiProviders", IsSecret: false, Description: "\"Simulated\", \"Anthropic\", \"OpenAI\" or \"Google\" - restart required to take effect."),
        new("AiProviders:EmbeddingProvider", "AiProviders", IsSecret: false, Description: "\"Simulated\", \"OpenAI\" or \"Google\" - restart required to take effect."),
        new("AiProviders:SimulatedFailureRatePercent", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:ApiKey", "AiProviders", IsSecret: true),
        new("AiProviders:Anthropic:Model", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:ApiVersion", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:BaseUrl", "AiProviders", IsSecret: false),
        new("AiProviders:OpenAI:ApiKey", "AiProviders", IsSecret: true),
        new("AiProviders:OpenAI:ChatModel", "AiProviders", IsSecret: false),
        new("AiProviders:OpenAI:EmbeddingModel", "AiProviders", IsSecret: false),
        new("AiProviders:OpenAI:BaseUrl", "AiProviders", IsSecret: false),
        new("AiProviders:Google:ApiKey", "AiProviders", IsSecret: true),
        new("AiProviders:Google:ChatModel", "AiProviders", IsSecret: false),
        new("AiProviders:Google:EmbeddingModel", "AiProviders", IsSecret: false),
        new("AiProviders:Google:BaseUrl", "AiProviders", IsSecret: false),

        // Media - tenant-overridable, see IsTenantOverridable's own doc comment.
        new("Media:MaxSizeBytes", "Media", IsSecret: false, IsTenantOverridable: true),
        new("Media:AllowedContentTypes", "Media", IsSecret: false, IsList: true, IsTenantOverridable: true),

        // Campaigns - tenant-overridable.
        new("Campaigns:MinStepMedia", "Campaigns", IsSecret: false, IsTenantOverridable: true),
        new("Campaigns:MaxStepMedia", "Campaigns", IsSecret: false, IsTenantOverridable: true),

        // Messaging - tenant-overridable.
        new("Messaging:MaxSendsPerRun", "Messaging", IsSecret: false, IsTenantOverridable: true),
        new("Messaging:MaxRetryAttempts", "Messaging", IsSecret: false, IsTenantOverridable: true),
        new("Messaging:RetryBackoffMinutes", "Messaging", IsSecret: false, IsList: true, IsTenantOverridable: true),
        new("Messaging:CustomerServiceWindowHours", "Messaging", IsSecret: false, IsTenantOverridable: true),

        // Ai - tenant-overridable.
        new("Ai:ConfidenceThreshold", "Ai", IsSecret: false, IsTenantOverridable: true),
        new("Ai:EscalationIntents", "Ai", IsSecret: false, IsList: true, IsTenantOverridable: true),
        new("Ai:KnowledgeBaseTopN", "Ai", IsSecret: false, IsTenantOverridable: true),
        new("Ai:MinRelevanceScore", "Ai", IsSecret: false, IsTenantOverridable: true),
        new("Ai:ConversationHistoryTurns", "Ai", IsSecret: false, IsTenantOverridable: true),
        new("Ai:MinFieldExtractionConfidence", "Ai", IsSecret: false, IsTenantOverridable: true),

        // MediaStorage - all three need a restart: Program.cs reads RootPath/PublicBasePath directly
        // off IConfiguration (not IOptionsSnapshot) to configure static-file-serving middleware once,
        // at startup, and LocalFileMediaStorageService resolves IOptions<LocalMediaStorageSettings>
        // (the non-live-reloading variant) at construction - neither observes a later change.
        new("MediaStorage:RootPath", "MediaStorage", IsSecret: false, Description: "Where uploaded media is written on disk - restart required to take effect."),
        new("MediaStorage:PublicBasePath", "MediaStorage", IsSecret: false, Description: "URL prefix media is served under - restart required to take effect."),
        new("MediaStorage:PublicBaseUrl", "MediaStorage", IsSecret: false, Description: "Scheme+host to prepend so Meta can fetch template media - restart required to take effect."),
    };
}
