using System.Text;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Stripe;
using WhatsAppSalesAutomation.Application.Billing;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Billing;
using WhatsAppSalesAutomation.Infrastructure.Identity;
using WhatsAppSalesAutomation.Infrastructure.Logging;
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
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();
        services.AddScoped<TenantStampingSaveChangesInterceptor>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IActiveTenantLookup, ActiveTenantLookup>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql => sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
            options.AddInterceptors(
                sp.GetRequiredService<AuditableEntitySaveChangesInterceptor>(),
                sp.GetRequiredService<TenantStampingSaveChangesInterceptor>());
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

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
        AddBilling(services, configuration);

        return services;
    }

    /// <summary>
    /// Stripe secret key/webhook secret are platform-global (one Stripe account for the whole
    /// platform - see StripeSettings' own doc comment), so unlike WhatsApp/AI they need no per-tenant
    /// factory: StripeClient is a single, thread-safe, DI-injected singleton (Stripe.net's documented
    /// alternative to the static StripeConfiguration.ApiKey), shared by every tenant's billing calls.
    /// </summary>
    private static void AddBilling(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StripeSettings>(configuration.GetSection("Stripe"));

        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<StripeSettings>>().Value;

            // Stripe.net's StripeClient constructor throws ArgumentException for an empty key - since
            // this is a singleton, that would fail at first resolution (i.e. the first billing
            // request of any kind, including GetPlansAsync, which never actually calls Stripe) rather
            // than only when a real Stripe call is attempted. A non-empty placeholder defers that
            // failure to the one place it belongs: inside an actual Stripe API call, which correctly
            // rejects the placeholder as an invalid key instead of taking down every billing endpoint
            // before Stripe:SecretKey is configured.
            var apiKey = string.IsNullOrEmpty(settings.SecretKey) ? "sk_not_configured" : settings.SecretKey;
            return new StripeClient(apiKey);
        });

        services.AddScoped<IBillingService, StripeBillingService>();
        services.AddScoped<IStripeWebhookHandler, StripeWebhookHandler>();
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

        // Pre-multi-tenant platform-level token store/refresher - MetaWhatsAppCloudApiClient no longer
        // reads from this (each tenant supplies their own AccessToken directly on TenantWhatsAppConfig
        // instead - BYO-WABA means auto-refresh is each tenant's own Meta App's concern). Kept
        // registered only because WhatsAppTokenRefreshJob (see AddHangfire) still depends on it;
        // expected to be revisited once per-tenant background jobs land.
        services.AddScoped<IWhatsAppTokenStore, WhatsAppTokenStore>();
        services.AddHttpClient<IWhatsAppTokenRefreshService, WhatsAppTokenRefreshService>();

        services.AddHttpClient<MetaWhatsAppCloudApiClient>();
        services.AddScoped<SimulatedWhatsAppClient>();
        services.AddScoped<IWhatsAppService, WhatsAppServiceFactory>();

        services.AddScoped<ITenantWhatsAppConfigProvider, TenantWhatsAppConfigProvider>();
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

        services.AddHttpClient<OpenAiEmbeddingClient>();
        services.AddHttpClient<GoogleEmbeddingClient>();
        services.AddScoped<SimulatedEmbeddingClient>();
        // The single "active" embedding provider (query-time retrieval) and the full catalog (used by
        // ReembedAsync to embed with every available provider at once) are two different DI
        // registrations against the same concrete clients - see IEmbeddingProviderCatalog's own doc
        // comment for why KnowledgeBaseService needs both.
        services.AddScoped<IEmbeddingService, TenantEmbeddingService>();
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

        services.AddScoped<CampaignInitialSenderJob>();
        services.AddScoped<FollowUpSchedulerJob>();
        services.AddScoped<MessageStatusRetryJob>();
        services.AddScoped<InboundWebhookProcessingJob>();
        services.AddScoped<WhatsAppTokenRefreshJob>();
        services.AddScoped<MessageTemplateSyncJob>();
    }
}
