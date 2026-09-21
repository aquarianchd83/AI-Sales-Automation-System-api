using System.Text;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Identity;
using WhatsAppSalesAutomation.Infrastructure.Logging;
using WhatsAppSalesAutomation.Application.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;
using WhatsAppSalesAutomation.Infrastructure.Realtime;
using WhatsAppSalesAutomation.Infrastructure.Services;
using WhatsAppSalesAutomation.Infrastructure.Settings;
using WhatsAppSalesAutomation.Infrastructure.Storage;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<AuditTrailSaveChangesInterceptor>();
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();
        services.AddScoped<TenantStampingSaveChangesInterceptor>();
        services.AddScoped<ITenantContext, TenantContext>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql => sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
            // The audit interceptor is FIRST, and the order matters: it adds AuditLog rows to the change
            // tracker during SavingChanges, and the two interceptors after it then timestamp and
            // tenant-stamp those rows like any other. Registered later, they would already have run.
            options.AddInterceptors(
                sp.GetRequiredService<AuditTrailSaveChangesInterceptor>(),
                sp.GetRequiredService<AuditableEntitySaveChangesInterceptor>(),
                sp.GetRequiredService<TenantStampingSaveChangesInterceptor>());
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        // Which vector store answers retrieval is decided once, at startup, by probing the server -
        // see SqlServerVectorCapability for why a probe rather than a version comparison, and why a
        // logged fallback rather than EC-22's hard failure. Singleton because the answer cannot
        // change without a server upgrade and a restart; the stores themselves are scoped, since
        // they hold the request's DbContext.
        services.AddSingleton<IVectorStoreCapability>(sp => new SqlServerVectorCapability(
            configuration.GetConnectionString("DefaultConnection")!,
            sp.GetRequiredService<ILogger<SqlServerVectorCapability>>()));

        // PDF needs a library the Application layer does not carry, so it registers its own extractor
        // into the same IDocumentFormatExtractor set DocumentTextExtractor composes.
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.IDocumentFormatExtractor, PdfDocumentExtractor>();
        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion.IKnowledgeIngestionQueue, HangfireKnowledgeIngestionQueue>();
        services.AddScoped<KnowledgeIndexingJob>();

        // Keyword leg: Full-Text where the server has it and the index exists, BM25 in the application
        // otherwise. Probed once, logged loudly - same shape and reasoning as the vector store above.
        services.AddSingleton<IKeywordSearchCapability>(sp => new SqlServerFullTextCapability(
            configuration.GetConnectionString("DefaultConnection")!,
            sp.GetRequiredService<ILogger<SqlServerFullTextCapability>>()));
        services.AddScoped<WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.IKeywordSearchStore>(sp =>
            sp.GetRequiredService<IKeywordSearchCapability>().SupportsFullText
                ? new SqlServerFullTextKeywordStore(sp.GetRequiredService<ApplicationDbContext>())
                : new Bm25KeywordStore(sp.GetRequiredService<ApplicationDbContext>()));

        services.AddMemoryCache();
        services.AddSingleton<WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.IRetrievalCache, MemoryRetrievalCache>();

        // Typed client, so the reranker gets an HttpClient with the platform's standard handler chain.
        services.AddHttpClient<WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval.IReranker, CohereReranker>();

        services.AddScoped<IVectorStore>(sp => sp.GetRequiredService<IVectorStoreCapability>().SupportsNativeVectors
            ? new SqlServerVectorStore(sp.GetRequiredService<ApplicationDbContext>())
            : new JsonColumnVectorStore(sp.GetRequiredService<ApplicationDbContext>()));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<JwtSettings>(configuration.GetSection("Jwt"));
        var jwtSettings = configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtSettings.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                // Browsers' native WebSocket API cannot attach an Authorization header, so SignalR's
                // documented pattern is a "access_token" query string parameter instead - only honoured
                // for the hub's own path, so this does not weaken bearer-header auth on every other route.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            context.Token = accessToken;

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddSignalR();

        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ICustomerImportService, CustomerImportService>();

        services.Configure<LocalMediaStorageSettings>(configuration.GetSection("MediaStorage"));
        services.AddScoped<IMediaStorageService, LocalFileMediaStorageService>();

        services.Configure<LocalLogFileReaderSettings>(configuration.GetSection("LogViewer"));
        services.AddScoped<ILogFileReaderService, LocalLogFileReaderService>();

        services.AddScoped<IAppSettingsStore, AppSettingsStore>();

        services.AddScoped<IWhatsAppWebhookParser, WhatsAppWebhookParser>();
        services.AddScoped<IWebhookSignatureValidator, WebhookSignatureValidator>();
        services.AddScoped<INotificationService, SignalRNotificationService>();

        AddWhatsAppClient(services, configuration);
        AddAiClients(services, configuration);
        AddHangfire(services, configuration);
        AddBilling(services);

        return services;
    }

    /// <summary>Payments are simulated for now - see IBillingService's own doc comment (Stripe pulled
    /// out for this platform's India-first launch, Razorpay not wired in yet) - so this is just the
    /// plain service registration every other Application-layer service gets, no external client/
    /// config binding needed the way Stripe's own singleton used to require.</summary>
    private static void AddBilling(IServiceCollection services)
    {
        services.AddScoped<IBillingService, BillingService>();
    }

    /// <summary>
    /// Provider *type* selection (Meta vs "Simulated" - no credentials needed) can no longer be one
    /// DI-time choice now that each tenant brings their own WhatsApp Business Account (or none at all)
    /// independently - <see cref="WhatsAppServiceFactory"/> is the DI-registered
    /// <see cref="IWhatsAppService"/> and picks per call, per tenant; both concrete clients are
    /// registered as plain concrete types (not interface-bound) so it can hold and delegate to either.
    /// </summary>
    private static void AddWhatsAppClient(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppSettings>(configuration.GetSection("WhatsApp"));

        // Per-tenant token refresh (WhatsAppTokenRefreshJob). RemoveAllLoggers is a security requirement,
        // not noise reduction: Meta's exchange takes client_secret and the access token as query
        // parameters, and IHttpClientFactory's default loggers write each request URL at Information -
        // a level this app's Serilog config keeps - so every tenant's secrets would otherwise land in the
        // log files the LogViewer screen serves.
        services.AddHttpClient<ITenantWhatsAppTokenRefreshService, TenantWhatsAppTokenRefreshService>()
            .RemoveAllLoggers();

        services.AddHttpClient<MetaWhatsAppCloudApiClient>();
        services.AddScoped<SimulatedWhatsAppClient>();
        services.AddScoped<IWhatsAppService, WhatsAppServiceFactory>();

        services.AddScoped<ITenantWhatsAppConfigProvider, TenantWhatsAppConfigProvider>();
        services.AddScoped<ITenantConfigOverrideProvider, TenantConfigOverrideProvider>();
        services.AddScoped<ITenantTimeZoneProvider, TenantTimeZoneProvider>();
    }

    /// <summary>
    /// Same per-tenant-router reasoning as <see cref="AddWhatsAppClient"/>: every concrete chat/
    /// embedding client is registered as a plain concrete type, and AiServiceFactory/
    /// TenantEmbeddingService/TenantEmbeddingProviderCatalog are the DI-registered IAiService/
    /// IEmbeddingService/IEmbeddingProviderCatalog, each picking per tenant per call. Two independent
    /// provider selections read from the same TenantAiProviderConfig - see AiProviderSettings' own
    /// (pre-multi-tenant) doc comment for why Provider (chat) and EmbeddingProvider are separate knobs;
    /// identical reasoning applies per-tenant now.
    /// </summary>
    private static void AddAiClients(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiProviderSettings>(configuration.GetSection("AiProviders"));

        services.AddScoped<ITenantAiConfigProvider, TenantAiConfigProvider>();
        services.AddScoped<IActiveAiProviderAccessor, ActiveAiProviderAccessor>();

        services.AddHttpClient<AnthropicAiClient>();
        services.AddHttpClient<OpenAiAiClient>();
        services.AddHttpClient<GoogleAiClient>();
        services.AddScoped<SimulatedAiClient>();
        services.AddScoped<IAiService, AiServiceFactory>();

        // Lead discovery (LeadDiscoveryJob) runs on the platform's own Anthropic key, not a tenant's - see
        // LeadDiscoveryAgentSettings. Provider is a startup-time choice, not a per-tenant one like the chat
        // clients above: "Simulated" invents businesses so the pipeline can be exercised without spending
        // anything, and every tenant's run uses whichever is configured.
        var leadDiscoverySection = configuration.GetSection("LeadDiscovery:Agent");
        services.Configure<LeadDiscoveryAgentSettings>(leadDiscoverySection);
        var leadDiscoverySettings = leadDiscoverySection.Get<LeadDiscoveryAgentSettings>() ?? new LeadDiscoveryAgentSettings();

        if (string.Equals(leadDiscoverySettings.Provider, "Simulated", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<ILeadDiscoveryAgent, SimulatedLeadDiscoveryAgent>();
        }
        else
        {
            // One request can run many web searches and fetches server-side before it responds, far beyond
            // HttpClient's 100-second default timeout.
            services.AddHttpClient<ILeadDiscoveryAgent, AnthropicLeadDiscoveryAgent>(
                client => client.Timeout = TimeSpan.FromMinutes(leadDiscoverySettings.RequestTimeoutMinutes));
        }

        services.AddHttpClient<OpenAiEmbeddingClient>();
        services.AddHttpClient<GoogleEmbeddingClient>();
        services.AddScoped<SimulatedEmbeddingClient>();
        // The single "active" embedding provider (query-time retrieval) and the full catalog (used by
        // ReembedAsync to embed with every available provider at once) are two different DI
        // registrations against the same concrete clients - see IEmbeddingProviderCatalog's own doc
        // comment for why KnowledgeBaseService needs both.
        services.AddScoped<IEmbeddingService, TenantEmbeddingService>();

        // The platform's own embedder, for platform-authored (GLOBAL) knowledge - see the type's doc.
        services.AddScoped<IPlatformEmbeddingService, PlatformEmbeddingService>();
        services.AddScoped<IEmbeddingProviderCatalog, TenantEmbeddingProviderCatalog>();
    }

    private static void AddHangfire(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(
                configuration.GetConnectionString("DefaultConnection"),
                new SqlServerStorageOptions
                {
                    // Own tables (Hangfire.*) in the same database - one connection string to manage,
                    // consistent with this project's single-database approach so far.
                    PrepareSchemaIfNecessary = true,
                    SchemaName = "Hangfire"
                }));

        // A dedicated worker process/queue is future work; embedding the server in the API process
        // is the simplest option for the traffic Phase 3 is designed for.
        services.AddHangfireServer();

        // The bridge between TenantJobTypes' string keys and these job classes - see
        // ITenantJobScheduler's own doc comment for why the Application layer cannot hold that mapping.
        // Singleton because every Hangfire service it wraps is one, and it keeps no per-request state.
        services.AddSingleton<ITenantJobScheduler, HangfireTenantJobScheduler>();

        // Singleton for the same reason: it creates its own per-run DI scope rather than depending on
        // one, which is exactly what lets it set the tenant before anything tenant-aware is resolved.
        services.AddSingleton<TenantJobRunner>();

        services.AddScoped<CampaignInitialSenderJob>();
        services.AddScoped<FollowUpSchedulerJob>();
        services.AddScoped<MessageStatusRetryJob>();
        services.AddScoped<InboundWebhookProcessingJob>();
        services.AddScoped<WhatsAppTokenRefreshJob>();
        services.AddScoped<MessageTemplateSyncJob>();
        services.AddScoped<LeadDiscoveryJob>();
        services.AddScoped<TenantJobReconciliationJob>();
        services.AddScoped<SubscriptionMaintenanceJob>();
        services.AddScoped<QuotaAlertJob>();

        services.Configure<WhatsAppSalesAutomation.Infrastructure.Notifications.SmtpOptions>(configuration.GetSection("Email:Smtp"));
        services.AddScoped<WhatsAppSalesAutomation.Application.Notifications.IEmailSender, WhatsAppSalesAutomation.Infrastructure.Notifications.SmtpEmailSender>();
        services.AddScoped<WhatsAppSalesAutomation.Application.Notifications.IPlatformWhatsAppSender, WhatsAppSalesAutomation.Infrastructure.Notifications.PlatformWhatsAppSender>();
    }
}
