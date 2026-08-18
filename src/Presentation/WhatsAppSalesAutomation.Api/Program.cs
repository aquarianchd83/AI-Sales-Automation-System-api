using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
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
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerDocumentation();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseSerilogRequestLogging();
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
