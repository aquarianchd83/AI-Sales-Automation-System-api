using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WhatsAppSalesAutomation.Api.Health;

/// <summary>
/// /health/live answers 200 whenever the process is running - for "restart it if this fails". /health/ready also needs the database -
/// for "send it traffic only if this passes". Anonymous, and the body is a status word only: no exception text, no versions, nothing a
/// stranger could use. Both sit behind the app's HTTPS redirect like everything else, so probe the address the proxy forwards.
/// </summary>
public static class HealthEndpoints
{
    public const string Ready = "ready";

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: new[] { Ready });
        return services;
    }

    public static IEndpointRouteBuilder MapAppHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteStatus });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(Ready), ResponseWriter = WriteStatus });
        return endpoints;
    }

    private static Task WriteStatus(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}
