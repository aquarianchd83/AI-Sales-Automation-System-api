using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Enrichers.CallerInfo;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Api.Logging;
using WhatsAppSalesAutomation.Api.Middleware;
using WhatsAppSalesAutomation.Application;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Infrastructure;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using WhatsAppSalesAutomation.Infrastructure.Realtime;
using WhatsAppSalesAutomation.Infrastructure.Settings;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithCallerInfo(includeFileInfo: false, assemblyPrefix: "WhatsAppSalesAutomation.")
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

        // Tags each event with the signed-in user's tenant (the "[t:...]" segment in the output
        // templates) so the Platform Admin Console's Logs screen can filter by tenant. Background jobs
        // and the anonymous webhook have no tenant claim and tag themselves via TenantLogScope.
        configuration.Enrich.With(new TenantLogEnricher(services.GetRequiredService<IHttpContextAccessor>()));

        // "LogViewer:EnableModuleLogging" (default on) - stamps every event with the calling
        // class/method (Namespace/Method properties, consumed by the {Namespace}/{Method}
        // placeholders in the Serilog:WriteTo output templates below), which is what LogsController's
        // module/method filters and GET .../logs/modules read back. It works by walking the stack
        // trace on every single log call, so this is the off switch for that overhead if it's ever
        // suspected under load - flip to false and restart to fall back to plain, un-attributed lines.
        // The assemblyPrefix keeps the walk from wasting time attributing framework-internal events
        // (ASP.NET Core, EF Core, Hangfire) to a frame that was never actually ours; those just get an
        // empty Namespace/Method, which LogService already treats as "unknown".
        if (context.Configuration.GetValue("LogViewer:EnableModuleLogging", true))
            configuration.Enrich.WithCallerInfo(includeFileInfo: false, assemblyPrefix: "WhatsAppSalesAutomation.");
    });

    // Feeds the AppSettings table (WhatsApp/AiProviders/Campaigns/Media/Messaging/Ai - see the "Move
    // config into DB" plan) into IConfiguration, added after the JSON providers so a DB row always
    // wins. AppSettingsConfigurationProvider.Load() falls back to appsettings.json values on its own
    // if the table doesn't exist yet (fresh DB, migrations haven't run below) - see its own doc
    // comment. IAppSettingsReloader is what SettingsController and AppSettingsSeeder call after a
    // write so every IOptionsSnapshot<T>/IOptionsMonitor<T> consumer picks it up with no restart.
    var appSettingsSource = new AppSettingsConfigurationSource(
        () => builder.Configuration.GetConnectionString("DefaultConnection")!,
        builder.Environment.ContentRootPath);
    // ConfigurationManager implements IConfigurationBuilder.Add explicitly, and an unrelated
    // ApplicationModelConventionExtensions.Add extension otherwise wins overload resolution on the
    // bare "builder.Configuration.Add(...)" call - the explicit interface cast is required here.
    ((IConfigurationBuilder)builder.Configuration).Add(appSettingsSource);
    builder.Services.AddSingleton<IAppSettingsReloader>(new AppSettingsReloader(appSettingsSource));

    // Encrypts AppSettingCatalog's IsSecret values (WhatsApp/AiProviders credentials) at rest - the
    // key ring lives on local disk (App_Data/keys), not in the DB row itself or a cloud secret
    // store, mirroring LocalFileMediaStorageService's App_Data/media convention.
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(AppSettingsSecretProtection.KeyRingPath(builder.Environment.ContentRootPath)))
        .SetApplicationName(AppSettingsSecretProtection.ApplicationName);

    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    // The frontend is served from one deployment reached via per-tenant subdomains under a wildcard
    // DNS record (acme.<domain>, mechicel.<domain>, ...) while the API stays on its own single origin
    // (api.<domain>) - every tenant subdomain is therefore a cross-origin caller of this API.
    // "Cors:AllowedOrigins" supports a single '*' wildcard segment per entry, e.g.
    // "https://*.saleautomation.com" - see IsOriginAllowed below.
    const string TenantCorsPolicy = "TenantSubdomains";
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(TenantCorsPolicy, policy => policy
            .SetIsOriginAllowed(origin => IsOriginAllowed(origin, corsOrigins))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    });

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerDocumentation();

    var app = builder.Build();

   // if (app.Environment.IsDevelopment())
   // {
        app.UseSwagger();
        app.UseSwaggerUI();
   // }

    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseSerilogRequestLogging();

    // Must run before UseHttpsRedirection: a TLS-terminating tunnel (ngrok, or any reverse proxy)
    // forwards plain HTTP to Kestrel on localhost, so without this the app sees an "insecure"
    // request and 307s to its own local HTTPS port (e.g. https://<public-host>:7080/...) - a port
    // nothing external forwards to, so Meta's webhook call just dead-ends. The immediate proxy here
    // is the ngrok agent connecting over loopback, which is within ForwardedHeadersMiddleware's
    // default trusted-proxy range, so no KnownProxies/KnownNetworks override is needed.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });
    app.UseHttpsRedirection();
    app.UseCors(TenantCorsPolicy);

    // Serves uploaded campaign media under MediaStorage:PublicBasePath. Local disk only, per
    // LocalFileMediaStorageService - swap for a cloud provider's own public URLs and this goes away.
    var mediaRootPath = builder.Configuration["MediaStorage:RootPath"] ?? "App_Data/media";
    var mediaPublicPath = builder.Configuration["MediaStorage:PublicBasePath"] ?? "/media";
    var mediaFullPath = Path.IsPathRooted(mediaRootPath) ? mediaRootPath : Path.Combine(app.Environment.ContentRootPath, mediaRootPath);
    Directory.CreateDirectory(mediaFullPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(mediaFullPath),
        RequestPath = mediaPublicPath
    });

    // Serves the small self-contained admin pages under wwwroot (e.g. /admin/knowledge-base.html) -
    // static HTML/JS hitting this same app's own API, same "no separate frontend project" reasoning
    // as the Hangfire dashboard below. Every page under here still calls [Authorize] API endpoints
    // with a real JWT it obtains via /api/v1/auth/login - this middleware only serves the static
    // shell, it does not bypass API authorization.
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHub<ConversationHub>("/hubs/conversations");

    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireDashboardAuthorizationFilter(app.Environment.IsDevelopment()) }
    });

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        await IdentitySeeder.SeedAsync(scope.ServiceProvider);

        // Inserts a DB row (defaulted from appsettings.json) for any AppSettingCatalog key that
        // doesn't have one yet - covers both the very first run against a fresh DB and any new key
        // added to the catalog in a later release. Then Reload() so this boot's first request
        // already sees the DB-backed values, not just the appsettings.json fallback
        // AppSettingsConfigurationProvider.Load() used above (the table didn't exist yet at that
        // point on a fresh DB).
        await AppSettingsSeeder.SeedDefaultsAsync(scope.ServiceProvider);
        scope.ServiceProvider.GetRequiredService<IAppSettingsReloader>().Reload();

        // The plan catalog signup/billing depend on - real data every environment needs, not a
        // Seed:* gated dev convenience, so this always runs (see PlanSeeder's own doc comment).
        await PlanSeeder.SeedAsync(scope.ServiceProvider);
        await QuotaCatalogSeeder.SeedAsync(scope.ServiceProvider);

        // The Content Management System's starter FAQ catalog - real customer-facing content every
        // environment needs, not a Seed:* gated dev convenience, so this always runs (see FaqSeeder's
        // own doc comment).
        await FaqSeeder.SeedAsync(scope.ServiceProvider);

        // Sample data for exploring the schema. No-ops unless Seed:DummyData is true.
        if (app.Environment.IsDevelopment())
            await DevDataSeeder.SeedAsync(scope.ServiceProvider);

        RecurringJobsRegistrar.RegisterAll(scope.ServiceProvider.GetRequiredService<IRecurringJobManager>());

        // Registers every active tenant's own recurring jobs from its TenantJobSchedules rows, creates
        // those rows for any tenant that has none yet, and drops registrations for tenants that are no
        // longer eligible or no longer exist. Runs on every boot (not just the first) because the table
        // is the source of truth for these schedules, not Hangfire's own storage - so a restart is also
        // how a deployment recovers from anything that drifted while it was down. Thereafter it is only
        // run on demand from the Platform Admin Console, plus a daily backstop pass
        // (TenantJobReconciliationJob) for when nobody is looking.
        await scope.ServiceProvider.GetRequiredService<ITenantJobProvisioner>().ReconcileAllAsync();
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Matches an Origin header against "Cors:AllowedOrigins" patterns, each of which may
/// contain a single '*' wildcard segment (e.g. "https://*.saleautomation.com" matches
/// "https://acme.saleautomation.com" but not "https://saleautomation.com" itself - list the bare
/// apex domain separately if it also needs to call the API).</summary>
static bool IsOriginAllowed(string origin, IReadOnlyList<string> patterns)
{
    foreach (var pattern in patterns)
    {
        var starIndex = pattern.IndexOf('*');
        if (starIndex < 0)
        {
            if (string.Equals(origin, pattern, StringComparison.OrdinalIgnoreCase))
                return true;
            continue;
        }

        var prefix = pattern[..starIndex];
        var suffix = pattern[(starIndex + 1)..];
        if (origin.Length >= prefix.Length + suffix.Length &&
            origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

// Exposed so WebApplicationFactory<Program> can be used for integration tests in a later phase.
public partial class Program
{
}
