using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Enrichers.CallerInfo;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Api.Middleware;
using WhatsAppSalesAutomation.Application;
using WhatsAppSalesAutomation.Infrastructure;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;
using WhatsAppSalesAutomation.Infrastructure.Realtime;

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

    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

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

        // Sample data for exploring the schema. No-ops unless Seed:DummyData is true.
        if (app.Environment.IsDevelopment())
            await DevDataSeeder.SeedAsync(scope.ServiceProvider);

        RecurringJobsRegistrar.RegisterAll(scope.ServiceProvider.GetRequiredService<IRecurringJobManager>());
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

// Exposed so WebApplicationFactory<Program> can be used for integration tests in a later phase.
public partial class Program
{
}
