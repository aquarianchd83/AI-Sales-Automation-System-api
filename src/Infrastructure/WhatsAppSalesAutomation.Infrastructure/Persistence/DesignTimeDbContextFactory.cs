using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model from THIS project rather than by booting the API.
///
/// Without it, every <c>migrations add</c> has to start the whole web host - which means it fails
/// whenever the API is already running and holding its own build output open, and which is why the
/// Phase 7 migration ended up hand-written. With it:
///
/// <code>dotnet ef migrations add Name --project src/Infrastructure/... --startup-project src/Infrastructure/...</code>
///
/// works while the application is running, against the same model the application uses.
///
/// It is never used at runtime - EF Core only looks for this type from its command-line tooling.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        // Read from the API's appsettings when it can be found, so a developer who changed the
        // connection string in one place does not have to change it in two. Falls back to a
        // placeholder because scaffolding a migration never opens a connection - only
        // "database update" does, and that is given its own --connection when it needs one.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("src/Presentation/WhatsAppSalesAutomation.Api/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Server=.;Database=WhatsAppSalesAutomation;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName))
            .Options;

        // The model's query filters close over these two, so they cannot be null - but nothing is
        // queried at design time, and a design-time tenant would be meaningless anyway. The stubs
        // below produce the platform's view of the model, which is the whole model.
        return new ApplicationDbContext(options, new DesignTimeTenantContext(), new DesignTimeUser());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;

        public bool IsPlatformSuperAdmin => true;

        public void SetTenant(Guid tenantId) { }
    }

    private sealed class DesignTimeUser : ICurrentUserService
    {
        public Guid? UserId => null;

        public string? Email => null;

        public IReadOnlyList<string> Roles => Array.Empty<string>();

        public Guid? TenantId => null;

        public Guid? ImpersonatorUserId => null;
    }
}
