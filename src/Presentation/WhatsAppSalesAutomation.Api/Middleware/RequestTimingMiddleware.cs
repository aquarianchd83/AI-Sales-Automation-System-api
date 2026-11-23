using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace WhatsAppSalesAutomation.Api.Middleware;

/// <summary>
/// Lightweight latency instrumentation for the p95 work: stamps every response with a <c>Server-Timing: app;dur=&lt;ms&gt;</c>
/// header (visible in the browser's network tab next to each call, so a slow endpoint is obvious without server access),
/// and decides the level the Serilog request log line is written at - requests slower than
/// <c>RequestTiming:SlowRequestThresholdMs</c> (default 500) are logged as Warning, so the Logs screen can list just the
/// slow ones by filtering on level.
/// </summary>
public class RequestTimingMiddleware
{
    public const string HeaderName = "Server-Timing";

    private readonly RequestDelegate _next;

    public RequestTimingMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();

        // OnStarting, not after _next: once the body starts streaming the headers are already sent. Measured to the moment
        // the response begins, which is what the caller waits for.
        context.Response.OnStarting(static state =>
        {
            var (ctx, start) = ((HttpContext, long))state;
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            ctx.Response.Headers.Append(HeaderName, $"app;dur={ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.CompletedTask;
        }, (context, started));

        return _next(context);
    }

    /// <summary>For <c>UseSerilogRequestLogging(o =&gt; o.GetLevel = ...)</c>: errors stay Error, slow requests become Warning.</summary>
    public static Func<HttpContext, double, Exception?, LogEventLevel> LevelFor(IConfiguration configuration)
    {
        var threshold = configuration.GetValue("RequestTiming:SlowRequestThresholdMs", 500d);
        return (ctx, elapsedMs, ex) =>
            ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
            : elapsedMs > threshold ? LogEventLevel.Warning
            : LogEventLevel.Information;
    }
}
