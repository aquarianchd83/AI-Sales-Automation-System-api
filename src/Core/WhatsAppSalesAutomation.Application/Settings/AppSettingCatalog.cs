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
        "WhatsApp", "AiProviders", "Campaigns", "Media", "Messaging", "Ai", "MediaStorage", "App", "Email", "Sms", "Retention", "PlatformWhatsApp", "Razorpay", "MetaAds"
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

        // MetaAds - the Facebook/Instagram ad-spend connection on the tenants' Settings and Revenue report. AppId/AppSecret may
        // be left empty to reuse the Meta App set up for WhatsApp (WhatsApp:AppId / WhatsApp:AppSecret). All are live: the
        // client reads them through IOptionsSnapshot. Needs the app's "Facebook Login" product with the redirect address added.
        new("MetaAds:AppId", "MetaAds", IsSecret: false, Description: "Meta App ID for the ad-spend login. Empty reuses WhatsApp:AppId."),
        new("MetaAds:AppSecret", "MetaAds", IsSecret: true, Description: "Meta App Secret for the ad-spend login. Empty reuses WhatsApp:AppSecret."),
        new("MetaAds:ApiVersion", "MetaAds", IsSecret: false, Description: "Graph API version, e.g. v21.0."),
        new("MetaAds:ApiBaseUrl", "MetaAds", IsSecret: false),
        new("MetaAds:DialogBaseUrl", "MetaAds", IsSecret: false, Description: "Where the Facebook login dialog is served from (https://www.facebook.com/)."),

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
        new("Ai:MaxFieldsToAsk", "Ai", IsSecret: false, IsTenantOverridable: true),
        new("Ai:HandoffOnHotLead", "Ai", IsSecret: false, IsTenantOverridable: true),

        // MediaStorage - all three need a restart: Program.cs reads RootPath/PublicBasePath directly
        // off IConfiguration (not IOptionsSnapshot) to configure static-file-serving middleware once,
        // at startup, and LocalFileMediaStorageService resolves IOptions<LocalMediaStorageSettings>
        // (the non-live-reloading variant) at construction - neither observes a later change.
        new("MediaStorage:RootPath", "MediaStorage", IsSecret: false, Description: "Where uploaded media is written on disk - restart required to take effect."),
        new("MediaStorage:PublicBasePath", "MediaStorage", IsSecret: false, Description: "URL prefix media is served under - restart required to take effect."),
        new("MediaStorage:PublicBaseUrl", "MediaStorage", IsSecret: false, Description: "Scheme+host to prepend so Meta can fetch template media - restart required to take effect."),
        // Where NEW uploads go. "S3" = the platform's own bucket, so tenants never need a cloud account; files already
        // stored keep working wherever they are. The S3 keys have no appsettings.json counterpart - they are managed on the
        // Platform Admin Console's AWS Settings page (PlatformAwsSettingsService) and live only in this table. All are live:
        // the storage services read them through IOptionsSnapshot.
        new("MediaStorage:Provider", "MediaStorage", IsSecret: false, Description: "Local (this server's disk) or S3 (the platform's bucket) for new uploads."),
        new("MediaStorage:S3:BucketName", "MediaStorage", IsSecret: false, Description: "The S3 bucket every tenant's media is stored in."),
        new("MediaStorage:S3:Region", "MediaStorage", IsSecret: false, Description: "The bucket's AWS region, e.g. ap-southeast-2."),
        new("MediaStorage:S3:AccessKeyId", "MediaStorage", IsSecret: true, Description: "AWS access key with put/get/delete on the bucket; leave empty to use the server's own AWS role."),
        new("MediaStorage:S3:SecretAccessKey", "MediaStorage", IsSecret: true, Description: "The secret for the access key above."),
        new("MediaStorage:S3:KeyPrefix", "MediaStorage", IsSecret: false, Description: "Folder inside the bucket (default media); the public-read bucket policy should cover it."),
        new("MediaStorage:S3:PublicBaseUrl", "MediaStorage", IsSecret: false, Description: "Optional CDN/custom domain in front of the bucket; empty uses the bucket's own address."),

        // How the platform reaches people for sign-in (password reset, verification). Managed on the Platform Admin Console's
        // Authentication Delivery page (PlatformDeliverySettingsService) and live only in this table. All are live: the
        // senders read them through IOptionsSnapshot.
        new("App:PublicUrl", "App", IsSecret: false, Description: "The web app's public address, used for links in emails. Never taken from a request."),
        new("Email:Smtp:Host", "Email", IsSecret: false, Description: "SMTP server. Empty means the platform sends no email."),
        new("Email:Smtp:Port", "Email", IsSecret: false),
        new("Email:Smtp:User", "Email", IsSecret: false),
        new("Email:Smtp:Password", "Email", IsSecret: true),
        new("Email:Smtp:From", "Email", IsSecret: false, Description: "The address emails are sent as."),
        new("Email:Smtp:EnableSsl", "Email", IsSecret: false),
        new("Sms:Msg91:Enabled", "Sms", IsSecret: false, Description: "Whether one-time codes are sent by SMS through MSG91."),
        new("Sms:Msg91:AuthKey", "Sms", IsSecret: true, Description: "MSG91 account auth key."),
        new("Sms:Msg91:OtpTemplateId", "Sms", IsSecret: false, Description: "MSG91 (DLT-registered) OTP template id."),
        new("Sms:Msg91:BaseUrl", "Sms", IsSecret: false, Description: "MSG91 API base address."),

        // The WhatsApp number the PLATFORM sends tenant notices from (plan expiring, credits running out). Managed on the Platform Admin
        // Console's WhatsApp page (PlatformWhatsAppSettingsService) and live only in this table. All are live: the sender reads them through
        // IOptionsSnapshot. Separate from the WhatsApp:* block above, which is the shared Meta app (webhooks), not a number to send from.
        new("PlatformWhatsApp:Enabled", "PlatformWhatsApp", IsSecret: false, Description: "Whether the platform sends tenant notices on WhatsApp. Absent means on."),
        new("PlatformWhatsApp:PhoneNumberId", "PlatformWhatsApp", IsSecret: false, Description: "Meta's id of the platform's WhatsApp number."),
        new("PlatformWhatsApp:WhatsAppBusinessAccountId", "PlatformWhatsApp", IsSecret: false, Description: "The platform's WhatsApp Business Account id (where its templates live)."),
        new("PlatformWhatsApp:AccessToken", "PlatformWhatsApp", IsSecret: true, Description: "Access token for the platform's number - a permanent System User token."),
        new("PlatformWhatsApp:ApiVersion", "PlatformWhatsApp", IsSecret: false),
        new("PlatformWhatsApp:ApiBaseUrl", "PlatformWhatsApp", IsSecret: false),

        // The platform's Razorpay account, kept on the Platform Admin Console's Razorpay test page (RazorpayService) and only in this table.
        new("Razorpay:KeyId", "Razorpay", IsSecret: false, Description: "API key id - rzp_test_... or rzp_live_..."),
        new("Razorpay:KeySecret", "Razorpay", IsSecret: true, Description: "API key secret. Signs checkout responses; never sent to a browser."),
        new("Razorpay:WebhookSecret", "Razorpay", IsSecret: true, Description: "The secret set on the Razorpay webhook; verifies every webhook delivery."),

        // Retention - how long data the platform only needs for a while is kept, in days. 0 keeps it forever; 1-6 counts as 7. Read live by
        // the daily clean-up. Conversations and messages are deliberately not here.
        new("Retention:WebhookEventDays", "Retention", IsSecret: false, Description: "Days to keep raw webhook payloads once processed. 0 = forever."),
        new("Retention:RefreshTokenDays", "Retention", IsSecret: false, Description: "Days to keep expired or revoked sign-in tokens. 0 = forever."),
        new("Retention:NotificationDays", "Retention", IsSecret: false, Description: "Days to keep notifications that have been read. 0 = forever."),
        new("Retention:AiInteractionDays", "Retention", IsSecret: false, Description: "Days to keep AI turn records. 0 = forever."),
        new("Retention:AuditLogDays", "Retention", IsSecret: false, Description: "Days to keep audit trail entries. 0 = forever."),
        new("Retention:BatchSize", "Retention", IsSecret: false, Description: "Rows removed per statement (100-5000)."),
    };
}
