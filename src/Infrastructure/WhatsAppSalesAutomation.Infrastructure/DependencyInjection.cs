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
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Identity;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;
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
            });

        services.AddAuthorization();
        services.AddHttpContextAccessor();

        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ICustomerImportService, CustomerImportService>();

        services.Configure<LocalMediaStorageSettings>(configuration.GetSection("MediaStorage"));
        services.AddScoped<IMediaStorageService, LocalFileMediaStorageService>();

        AddWhatsAppClient(services, configuration);
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
    }
}
