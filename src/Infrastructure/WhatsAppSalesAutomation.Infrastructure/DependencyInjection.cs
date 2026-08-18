using System.Text;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Identity;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;
using WhatsAppSalesAutomation.Infrastructure.Realtime;
using WhatsAppSalesAutomation.Infrastructure.Services;
using WhatsAppSalesAutomation.Infrastructure.Storage;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<AuditableEntitySaveChangesInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql => sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
            options.AddInterceptors(sp.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
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

        services.AddScoped<IWhatsAppWebhookParser, WhatsAppWebhookParser>();
        services.AddScoped<IWebhookSignatureValidator, WebhookSignatureValidator>();
        services.AddScoped<INotificationService, SignalRNotificationService>();

        AddWhatsAppClient(services, configuration);
        AddAiClients(services, configuration);
        AddHangfire(services, configuration);

        return services;
    }

    /// <summary>
    /// "Simulated" (default) needs no credentials and lets the whole campaign pipeline run and be
    /// tested without a live WhatsApp Business Account; "Meta" is the real Cloud API client. See
    /// the Phase 1 open assumptions - no account was available when this was built.
    /// </summary>
    private static void AddWhatsAppClient(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppSettings>(configuration.GetSection("WhatsApp"));
        var provider = configuration.GetSection("WhatsApp")["Provider"] ?? "Simulated";

        if (string.Equals(provider, "Meta", StringComparison.OrdinalIgnoreCase))
            services.AddHttpClient<IWhatsAppService, MetaWhatsAppCloudApiClient>();
        else
            services.AddScoped<IWhatsAppService, SimulatedWhatsAppClient>();
    }

    /// <summary>
    /// Two independent provider selections read from the same AiProviderSettings - see its own doc
    /// comment for why Provider (chat) and EmbeddingProvider are separate knobs. Both default to
    /// "Simulated", same zero-credentials-needed reasoning as AddWhatsAppClient.
    /// </summary>
    private static void AddAiClients(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiProviderSettings>(configuration.GetSection("AiProviders"));
        var section = configuration.GetSection("AiProviders");
        var provider = section["Provider"] ?? "Simulated";
        var embeddingProvider = section["EmbeddingProvider"] ?? "Simulated";

        switch (provider)
        {
            case var p when string.Equals(p, "Anthropic", StringComparison.OrdinalIgnoreCase):
                services.AddHttpClient<IAiService, AnthropicAiClient>();
                break;
            case var p when string.Equals(p, "OpenAI", StringComparison.OrdinalIgnoreCase):
                services.AddHttpClient<IAiService, OpenAiAiClient>();
                break;
            case var p when string.Equals(p, "Google", StringComparison.OrdinalIgnoreCase):
                services.AddHttpClient<IAiService, GoogleAiClient>();
                break;
            default:
                services.AddScoped<IAiService, SimulatedAiClient>();
                break;
        }

        switch (embeddingProvider)
        {
            case var p when string.Equals(p, "OpenAI", StringComparison.OrdinalIgnoreCase):
                services.AddHttpClient<IEmbeddingService, OpenAiEmbeddingClient>();
                break;
            case var p when string.Equals(p, "Google", StringComparison.OrdinalIgnoreCase):
                services.AddHttpClient<IEmbeddingService, GoogleEmbeddingClient>();
                break;
            default:
                services.AddScoped<IEmbeddingService, SimulatedEmbeddingClient>();
                break;
        }
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
    }
}
