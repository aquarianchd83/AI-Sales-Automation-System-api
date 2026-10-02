using Microsoft.Extensions.Diagnostics.HealthChecks;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Api.Health;

/// <summary>Can the app reach its database right now? What a load balancer needs to know before sending it traffic.</summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly ApplicationDbContext _db;

    public DatabaseHealthCheck(ApplicationDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database is not reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reason stays in the logs, not in a response anyone can read.
            return HealthCheckResult.Unhealthy("The database check failed.", ex);
        }
    }
}
