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
/// <param name="DefaultValue">What the seeder stores when appsettings.json has nothing for the key, so a setting managed only in the Platform Admin
/// Console still shows a value on first run.</param>
/// <param name="Options">When set, the value must be one of these: the console shows a dropdown instead of a text box and a save of anything else
/// is refused.</param>
/// <param name="IsTenantOverridable">True for the subset of keys a PlatformSuperAdmin can also override
/// per-tenant from a tenant's detail page (see ITenantConfigOverrideProvider) - business tuning knobs
/// (Campaigns/Media/Messaging/Ai) where different tenants reasonably want different values. False for
/// everything else here: WhatsApp has its own bespoke per-tenant mechanism
/// (TenantWhatsAppConfig), AiProviders is one platform-wide config with no per-tenant form, and MediaStorage is platform infra shared by every
/// tenant (one file store) - it doesn't belong in a per-tenant override table either.</param>
public record AppSettingDefinition(
    string Key,
    string Category,
    bool IsSecret,
    bool IsList = false,
    string? Description = null,
    bool IsTenantOverridable = false,
    string? DefaultValue = null,
    IReadOnlyList<string>? Options = null);

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
        "WhatsApp", "AiProviders", "Campaigns", "Media", "Messaging", "Ai", "MediaStorage", "App", "Email", "Sms", "Integrations", "Retention", "PlatformWhatsApp", "WhatsAppProvider", "WhatsAppBridge", "Razorpay", "MetaAds", "ContentStudio"
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
        new("WhatsApp:EmbeddedSignupConfigId", "WhatsApp", IsSecret: false, Description: "Meta Embedded Signup configuration id - turns on \"Connect with Meta\" for tenants. Needs WhatsApp:AppId and WhatsApp:AppSecret too."),
        new("WhatsApp:EmbeddedSignupEnabled", "WhatsApp", IsSecret: false, Description: "Switch for \"Connect with Meta\" on tenants' Settings. On unless set to false; switching it off keeps the app id, secret and config id."),

        // MetaAds - the Facebook/Instagram ad-spend connection on the tenants' Settings and Revenue report. AppId/AppSecret may
        // be left empty to reuse the Meta App set up for WhatsApp (WhatsApp:AppId / WhatsApp:AppSecret). All are live: the
        // client reads them through IOptionsSnapshot. Needs the app's "Facebook Login" product with the redirect address added.
        new("MetaAds:AppId", "MetaAds", IsSecret: false, Description: "Meta App ID for the ad-spend login. Empty reuses WhatsApp:AppId."),
        new("MetaAds:AppSecret", "MetaAds", IsSecret: true, Description: "Meta App Secret for the ad-spend login. Empty reuses WhatsApp:AppSecret."),
        new("MetaAds:ApiVersion", "MetaAds", IsSecret: false, Description: "Graph API version, e.g. v21.0."),
        new("MetaAds:ApiBaseUrl", "MetaAds", IsSecret: false),
        new("MetaAds:DialogBaseUrl", "MetaAds", IsSecret: false, Description: "Where the Facebook login dialog is served from (https://www.facebook.com/)."),

        // ContentStudio - the AI Content Studio module. Live (read through IOptionsSnapshot). Each key is a platform default that a tenant can override from its
        // own Advanced settings, so a tenant such as the pilot can have the studio switched on while everyone else stays off.
        new("ContentStudio:Enabled", "ContentStudio", IsSecret: false, Description: "\"true\" turns the AI Content Studio on for tenants. Off by default.", IsTenantOverridable: true, DefaultValue: "false", Options: new[] { "true", "false" }),
        new("ContentStudio:StorageMode", "ContentStudio", IsSecret: false, Description: "\"PublicBucket\" (the platform's existing public bucket, files reached by unguessable keys) or \"SignedUrls\" (short-lived addresses). Switching needs no migration.", IsTenantOverridable: true, DefaultValue: "PublicBucket", Options: new[] { "PublicBucket", "SignedUrls" }),
        new("ContentStudio:StorageKeyPrefix", "ContentStudio", IsSecret: false, Description: "Folder studio files are stored under. Default \"studio\".", IsTenantOverridable: true, DefaultValue: "studio"),
        new("ContentStudio:SignedUrlMinutes", "ContentStudio", IsSecret: false, Description: "How long a signed address stays valid, in minutes. Only used in SignedUrls mode.", IsTenantOverridable: true, DefaultValue: "60", Options: new[] { "15", "30", "60", "120", "240" }),
        new("ContentStudio:ReadinessRules", "ContentStudio", IsSecret: false, Description: "JSON that decides what each kind of content needs to be ready (requirements, weights, critical items, minimum scores). Leave empty to use the built-in rules. An invalid value is ignored and the built-in rules are used.", DefaultValue: ""),

        // AiProviders - the single, platform-wide AI configuration every tenant runs on. All live (read through IOptionsSnapshot).
        new("AiProviders:Provider", "AiProviders", IsSecret: false, Description: "\"Simulated\", \"Anthropic\", \"OpenAI\" or \"Google\" - the one provider every tenant's AI chat runs on. Live once saved."),
        new("AiProviders:EmbeddingProvider", "AiProviders", IsSecret: false, Description: "\"Simulated\", \"OpenAI\" or \"Google\" - provider that embeds knowledge-base text for every tenant. Live once saved."),
        new("AiProviders:SimulatedFailureRatePercent", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:Enabled", "AiProviders", IsSecret: false, Description: "\"true\" (On) or \"false\" (Off). Off turns Claude off across the whole application: it is not used for chat, embeddings, suggestions or lead discovery, and anything set to it runs on the simulator. Live once saved.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("AiProviders:Anthropic:ApiKey", "AiProviders", IsSecret: true),
        new("AiProviders:Anthropic:Model", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:ApiVersion", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:BaseUrl", "AiProviders", IsSecret: false),
        new("AiProviders:Anthropic:InputPer1MUsd", "AiProviders", IsSecret: false, Description: "Price of Claude input for the model above, US dollars per million tokens. Copy it from the provider's price list - it is only used to estimate cost.", DefaultValue: "0"),
        new("AiProviders:Anthropic:OutputPer1MUsd", "AiProviders", IsSecret: false, Description: "Price of Claude output for the model above, US dollars per million tokens.", DefaultValue: "0"),
        new("AiProviders:OpenAI:Enabled", "AiProviders", IsSecret: false, Description: "\"true\" (On) or \"false\" (Off). Off turns ChatGPT off across the whole application: it is not used for chat, embeddings, suggestions or lead discovery, and anything set to it runs on the simulator. Live once saved.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("AiProviders:OpenAI:ApiKey", "AiProviders", IsSecret: true),
        new("AiProviders:OpenAI:ChatModel", "AiProviders", IsSecret: false),
        new("AiProviders:OpenAI:EmbeddingModel", "AiProviders", IsSecret: false),
        new("AiProviders:OpenAI:BaseUrl", "AiProviders", IsSecret: false),
        new("AiProviders:OpenAI:InputPer1MUsd", "AiProviders", IsSecret: false, Description: "Price of ChatGPT input for the model above, US dollars per million tokens. Copy it from the provider's price list - it is only used to estimate cost.", DefaultValue: "0"),
        new("AiProviders:OpenAI:OutputPer1MUsd", "AiProviders", IsSecret: false, Description: "Price of ChatGPT output for the model above, US dollars per million tokens.", DefaultValue: "0"),
        new("AiProviders:Google:Enabled", "AiProviders", IsSecret: false, Description: "\"true\" (On) or \"false\" (Off). Off turns Gemini off across the whole application: it is not used for chat, embeddings, suggestions or lead discovery, and anything set to it runs on the simulator. Live once saved.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("AiProviders:Google:ApiKey", "AiProviders", IsSecret: true),
        new("AiProviders:Google:ChatModel", "AiProviders", IsSecret: false),
        new("AiProviders:Google:EmbeddingModel", "AiProviders", IsSecret: false),
        new("AiProviders:Google:BaseUrl", "AiProviders", IsSecret: false),
        new("AiProviders:Google:InputPer1MUsd", "AiProviders", IsSecret: false, Description: "Price of Gemini input for the model above, US dollars per million tokens. Copy it from the provider's price list - it is only used to estimate cost.", DefaultValue: "0"),
        new("AiProviders:Google:OutputPer1MUsd", "AiProviders", IsSecret: false, Description: "Price of Gemini output for the model above, US dollars per million tokens.", DefaultValue: "0"),

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
        new("Messaging:DailyTemplateCap", "Messaging", IsSecret: false, IsTenantOverridable: true),
        new("Messaging:MaxMarketingPerContactPerWindow", "Messaging", IsSecret: false, IsTenantOverridable: true),
        new("Messaging:MarketingWindowDays", "Messaging", IsSecret: false, IsTenantOverridable: true),
        new("Messaging:CapStepUpAfterGreenDays", "Messaging", IsSecret: false, IsTenantOverridable: true),
        new("Messaging:CapStepUps", "Messaging", IsSecret: false, IsList: true, IsTenantOverridable: true),

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

        // How the consent SMS and the DND check are handled: the built-in simulator or a third-party API. Kept on the Platform Admin
        // Console's Integrations page (PlatformIntegrationSettingsService) and live only in this table; absent means the simulator.
        new("Integrations:ConsentSms:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how the WhatsApp consent SMS is sent.", Options: new[] { "Simulator", "Api" }),
        new("Integrations:ConsentSms:ApiUrl", "Integrations", IsSecret: false, Description: "Consent SMS third-party API address (https)."),
        new("Integrations:ConsentSms:ApiKey", "Integrations", IsSecret: true, Description: "Consent SMS third-party API key."),
        new("Integrations:ConsentSms:ApiKeyHeader", "Integrations", IsSecret: false, Description: "Header the key is sent in (Authorization sends it as a Bearer token)."),
        new("Integrations:ConsentSms:SenderId", "Integrations", IsSecret: false, Description: "Sender id the SMS is sent as."),
        new("Integrations:ConsentSms:TimeoutSeconds", "Integrations", IsSecret: false),
        new("Integrations:DndCheck:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how the DND check is done.", Options: new[] { "Simulator", "Api" }),
        new("Integrations:DndCheck:ApiUrl", "Integrations", IsSecret: false, Description: "DND check third-party API address (https)."),
        new("Integrations:DndCheck:ApiKey", "Integrations", IsSecret: true, Description: "DND check third-party API key."),
        new("Integrations:DndCheck:ApiKeyHeader", "Integrations", IsSecret: false, Description: "Header the key is sent in (Authorization sends it as a Bearer token)."),
        new("Integrations:DndCheck:SenderId", "Integrations", IsSecret: false),
        new("Integrations:DndCheck:TimeoutSeconds", "Integrations", IsSecret: false),

        // Which engine each AI feature runs on: the simulator or ChatGPT / Claude / Gemini (the keys are the AiProviders:* ones). Kept on the
        // Integrations page (PlatformAiFeatureSettingsService). Discovery leads default to Claude, the only tool they support.
        new("Integrations:Ai:DiscoveryLead:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how discovery lead keyword suggestions are produced.", DefaultValue: "Api", Options: new[] { "Simulator", "Api" }),
        new("Integrations:Ai:DiscoveryLead:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for discovery leads. Only Anthropic (Claude) is supported.", DefaultValue: "Anthropic", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:KnowledgeBase:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how the knowledge base is produced.", DefaultValue: "Simulator", Options: new[] { "Simulator", "Api" }),
        new("Integrations:Ai:KnowledgeBase:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for the knowledge base: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:ProductDocumentReading:Mode", "Integrations", IsSecret: false, Description: "\"Off\", \"Simulator\" or \"Api\" - product knowledge: reading documents and PDFs.", DefaultValue: "Simulator", Options: new[] { "Off", "Simulator", "Api" }),
        new("Integrations:Ai:ProductDocumentReading:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for it: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:ProductScannedReading:Mode", "Integrations", IsSecret: false, Description: "\"Off\", \"Simulator\" or \"Api\" - product knowledge: reading scanned pages and images (page images are sent to the provider).", DefaultValue: "Off", Options: new[] { "Off", "Simulator", "Api" }),
        new("Integrations:Ai:ProductScannedReading:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for it: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:ProductSuggestedAnswers:Mode", "Integrations", IsSecret: false, Description: "\"Off\", \"Simulator\" or \"Api\" - product knowledge: suggested answers.", DefaultValue: "Simulator", Options: new[] { "Off", "Simulator", "Api" }),
        new("Integrations:Ai:ProductSuggestedAnswers:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for it: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:ProductQuestionnaire:Mode", "Integrations", IsSecret: false, Description: "\"Off\", \"Simulator\" or \"Api\" - product knowledge: questionnaire and benefits.", DefaultValue: "Simulator", Options: new[] { "Off", "Simulator", "Api" }),
        new("Integrations:Ai:ProductQuestionnaire:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for it: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:ProductGapCheck:Mode", "Integrations", IsSecret: false, Description: "\"Off\", \"Simulator\" or \"Api\" - product knowledge: the gaps and conflicts check.", DefaultValue: "Simulator", Options: new[] { "Off", "Simulator", "Api" }),
        new("Integrations:Ai:ProductGapCheck:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for it: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:ProductKnowledgeDraft:Mode", "Integrations", IsSecret: false, Description: "\"Off\", \"Simulator\" or \"Api\" - product knowledge: the draft knowledge file.", DefaultValue: "Simulator", Options: new[] { "Off", "Simulator", "Api" }),
        new("Integrations:Ai:ProductKnowledgeDraft:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for it: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:ProductSources:DocumentUpload", "Integrations", IsSecret: false, Description: "\"true\" lets tenants upload documents (brochures, PDFs, manuals) for product knowledge and have their text read. \"false\" stops new uploads and re-reads; what was read stays.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("Integrations:ProductSources:WebsiteScanning", "Integrations", IsSecret: false, Description: "\"true\" lets tenants add their website and have its public pages read. \"false\" stops new websites and re-scans; what was read stays.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("ProductKnowledge:DraftPassages", "ProductKnowledge", IsSecret: false, Description: "How many of the best-matching source passages are given to the AI to answer one question (2-20). More passages cost more per answer.", DefaultValue: "10"),
        new("ProductKnowledge:ServeChat", "ProductKnowledge", IsSecret: false, IsTenantOverridable: true, Description: "\"true\" makes customer chat answer from the tenant's published product knowledge only (the approved answers of its current published version) instead of the knowledge base articles. Off by default; switch it on per tenant under Tenants > the tenant > Settings overrides. The published version is indexed either way, so switching on takes effect at once.", DefaultValue: "false", Options: new[] { "true", "false" }),
        new("ProductKnowledge:MaxPassageChars", "ProductKnowledge", IsSecret: false, Description: "Longest stretch of one source passage shown to the AI when drafting (800-4000 characters). Passages are at most 1600 characters when read, so the default cuts nothing.", DefaultValue: "2000"),
        new("ProductKnowledge:HybridRetrieval", "ProductKnowledge", IsSecret: false, Description: "\"true\" finds source passages by meaning (embeddings) as well as by words when drafting answers; the platform pays for the embeddings. \"false\" uses words only. With the simulated embedding provider words are always used.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("ProductKnowledge:MaxCustomQuestions", "ProductKnowledge", IsSecret: false, Description: "Most questions of their own one tenant may keep (the standard questions do not count).", DefaultValue: "20"),
        new("ProductKnowledge:MaterialReviewDays", "ProductKnowledge", IsSecret: false, Description: "How many days an approved price, guarantee, certification or result stays current. After that the tenant must confirm it again before it is published (7-730).", DefaultValue: "180"),
        new("ProductKnowledge:MaxFileMb", "ProductKnowledge", IsSecret: false, Description: "Largest document a tenant may upload, in MB. Applies to new uploads.", DefaultValue: "10"),
        new("ProductKnowledge:MaxSourcesPerTenant", "ProductKnowledge", IsSecret: false, Description: "Most documents and websites one tenant may keep (deleted ones do not count).", DefaultValue: "50"),
        new("ProductKnowledge:MaxScannedPages", "ProductKnowledge", IsSecret: false, Description: "Most scanned pages of one document read with the AI provider - each page is one AI call, paid by the platform.", DefaultValue: "20"),
        new("ProductKnowledge:CrawlMaxPages", "ProductKnowledge", IsSecret: false, Description: "Most pages read from one website per scan.", DefaultValue: "50"),
        new("ProductKnowledge:CrawlMaxDepth", "ProductKnowledge", IsSecret: false, Description: "How many links deep from the start page a website scan follows (0 = the start page only).", DefaultValue: "2"),
        new("ProductKnowledge:CrawlPageTimeoutSeconds", "ProductKnowledge", IsSecret: false, Description: "Longest wait for one website page, in seconds.", DefaultValue: "15"),
        new("ProductKnowledge:CrawlMaxPageKb", "ProductKnowledge", IsSecret: false, Description: "Largest website page read, in KB. Bigger pages are skipped.", DefaultValue: "2048"),
        new("ProductKnowledge:CrawlRespectRobots", "ProductKnowledge", IsSecret: false, Description: "\"true\" obeys the site's robots.txt (recommended). \"false\" ignores it - only for the tenant's own site, with their consent.", DefaultValue: "true", Options: new[] { "true", "false" }),
        new("Integrations:Ai:Policy:FallbackEnabled", "Integrations", IsSecret: false, Description: "\"true\" lets product-knowledge AI tasks try the next provider in the fallback order when one fails. Off by default; the simulator is never used as a fallback.", DefaultValue: "false", Options: new[] { "true", "false" }),
        new("Integrations:Ai:Policy:FallbackOrder", "Integrations", IsSecret: false, Description: "Providers to try after the chosen one, in order, separated by commas: OpenAI, Anthropic, Google.", DefaultValue: "OpenAI,Google,Anthropic"),
        new("Integrations:Ai:Policy:SimulatorScenario", "Integrations", IsSecret: false, Description: "What the simulator returns for product-knowledge tasks: a normal answer, missing evidence, conflicting sources, a malformed reply, or a provider error. For testing.", DefaultValue: "Success", Options: new[] { "Success", "MissingEvidence", "Conflict", "Malformed", "ProviderError" }),
        new("Integrations:Ai:Policy:MaxCostPerCallUsd", "Integrations", IsSecret: false, Description: "A single AI call whose estimated cost would exceed this many US dollars is skipped. 0 means no limit.", DefaultValue: "0.50"),
        new("Integrations:Ai:Policy:TimeoutSeconds", "Integrations", IsSecret: false, Description: "How long one AI call may take before it is treated as failed.", DefaultValue: "60"),
        new("Integrations:Ai:Policy:MaxRetriesPerProvider", "Integrations", IsSecret: false, Description: "How many times a provider is retried after a temporary failure (timeout, rate limit, server error). Not used for calls with side effects.", DefaultValue: "1"),
        new("Integrations:Ai:Policy:RetryDelayMs", "Integrations", IsSecret: false, Description: "Pause before a retry, in milliseconds.", DefaultValue: "500"),
        new("Integrations:Ai:ContentGeneration:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how content generation is produced.", DefaultValue: "Simulator", Options: new[] { "Simulator", "Api" }),
        new("Integrations:Ai:ContentGeneration:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for content generation: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:VideoRenders:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how video renders are produced.", DefaultValue: "Simulator", Options: new[] { "Simulator", "Api" }),
        new("Integrations:Ai:VideoRenders:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for video renders: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "OpenAI", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:BusinessKeywords:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how the domain keyword suggestions are produced.", DefaultValue: "Api", Options: new[] { "Simulator", "Api" }),
        new("Integrations:Ai:DiscoveryKeywords:Mode", "Integrations", IsSecret: false, Description: "\"Simulator\" or \"Api\" - how the lead discovery AI suggest is produced.", DefaultValue: "Api", Options: new[] { "Simulator", "Api" }),
        new("Integrations:Ai:DiscoveryKeywords:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for the lead discovery AI suggest: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "Anthropic", Options: new[] { "OpenAI", "Anthropic", "Google" }),
        new("Integrations:Ai:BusinessKeywords:Provider", "Integrations", IsSecret: false, Description: "Third-party tool for the domain keyword suggestions: OpenAI (ChatGPT), Anthropic (Claude) or Google (Gemini).", DefaultValue: "Anthropic", Options: new[] { "OpenAI", "Anthropic", "Google" }),

        // The WhatsApp number the PLATFORM sends tenant notices from (plan expiring, credits running out). Managed on the Platform Admin
        // Console's WhatsApp page (PlatformWhatsAppSettingsService) and live only in this table. All are live: the sender reads them through
        // IOptionsSnapshot. Separate from the WhatsApp:* block above, which is the shared Meta app (webhooks), not a number to send from.
        // Which messaging integration the platform offers tenants. Managed on the Platform Admin Console's WhatsApp provider card
        // (WhatsAppProviderSettingsService); only "Meta" can be activated until the QR bridge adapter ships.
        // The self-hosted QR bridge (Evolution API). Managed on the Platform Admin Console; the key is encrypted at rest. All live (IOptionsSnapshot).
        new("WhatsAppBridge:BaseUrl", "WhatsAppBridge", IsSecret: false, Description: "Address of the self-hosted bridge, e.g. https://bridge.example.com."),
        new("WhatsAppBridge:ApiKey", "WhatsAppBridge", IsSecret: true, Description: "The bridge's global API key."),
        new("WhatsAppBridge:WebhookBaseUrl", "WhatsAppBridge", IsSecret: false, Description: "Public address of THIS API that the bridge calls back on."),
        new("WhatsAppBridge:PilotTenantIds", "WhatsAppBridge", IsSecret: false, IsList: true, Description: "While set, only these tenants use the bridge when it is the active provider; others stay on Meta. Empty: every tenant."),
        new("WhatsAppProvider:Active", "WhatsAppProvider", IsSecret: false, DefaultValue: "Meta", Description: "The active WhatsApp provider: \"Meta\" (Cloud API) or \"QrBridge\".", Options: new[] { "Meta", "QrBridge" }),
        new("WhatsAppProvider:TenantOverrides", "WhatsAppProvider", IsSecret: false, IsList: true, Description: "Per-tenant provider choices as tenantId=Provider (Meta or QrBridge). A tenant listed here ignores the active provider."),
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
