using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Api.Extensions;

/// <summary>
/// Wires ASP.NET Core's built-in rate limiter from <see cref="RateLimitOptions"/>.
///
/// Three named policies, applied per endpoint with <c>[EnableRateLimiting]</c>, plus a global limiter
/// that covers everything else. The global one is a backstop, not the main event: a controller that
/// needs a specific budget says so on itself, and anything nobody thought about still lands in a
/// bucket rather than none.
/// </summary>
public static class RateLimitingServiceExtensions
{
    /// <summary>Credential-facing endpoints - login, signup, refresh-token. Per client IP.</summary>
    public const string AuthPolicy = "auth";

    /// <summary>The unauthenticated provider webhooks. Per client IP.</summary>
    public const string WebhookPolicy = "webhook";

    /// <summary>Ordinary authenticated API traffic. Per user, falling back to IP.</summary>
    public const string ApiPolicy = "api";

    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("RateLimiting").Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.Configure<RateLimitOptions>(configuration.GetSection("RateLimiting"));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // A 429 with no Retry-After tells a well-behaved client nothing except "try again at
            // random", which in practice means "try again immediately". The window length is the
            // honest answer for a fixed window: waiting that long is guaranteed to work.
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
                    ? metadata
                    : TimeSpan.FromSeconds(options.Api.WindowSeconds);

                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);

                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("WhatsAppSalesAutomation.Api.RateLimiting");

                // Warning, not Information: on the auth policy this line IS the brute-force signal,
                // and the Logs screen's default filter starts at Warning.
                logger.LogWarning(
                    "Rate limit rejected {Method} {Path} from {RemoteIp} (user {UserId})",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value,
                    ClientIp(context.HttpContext),
                    context.HttpContext.User.Identity?.IsAuthenticated == true
                        ? context.HttpContext.User.Identity.Name ?? "authenticated"
                        : "anonymous");

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    title = "Too many requests.",
                    status = StatusCodes.Status429TooManyRequests,
                    retryAfterSeconds = (int)retryAfter.TotalSeconds
                }, cancellationToken);
            };

            if (!options.Enabled)
            {
                // Every policy still has to exist - the [EnableRateLimiting("auth")] attributes
                // reference them by name and throw at startup if one is missing. NoLimiter partitions
                // make them no-ops, which is what "Enabled: false" should mean: the app behaves
                // identically minus the limiting, rather than failing to start.
                limiter.AddPolicy(AuthPolicy, _ => RateLimitPartition.GetNoLimiter("disabled"));
                limiter.AddPolicy(WebhookPolicy, _ => RateLimitPartition.GetNoLimiter("disabled"));
                limiter.AddPolicy(ApiPolicy, _ => RateLimitPartition.GetNoLimiter("disabled"));
                return;
            }

            limiter.AddPolicy(AuthPolicy, context => PartitionByIp(context, options, options.Auth, AuthPolicy));
            limiter.AddPolicy(WebhookPolicy, context => PartitionByIp(context, options, options.Webhook, WebhookPolicy));
            limiter.AddPolicy(ApiPolicy, context => PartitionByUser(context, options, options.Api, ApiPolicy));

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => PartitionByUser(context, options, options.Api, "global"));
        });

        return services;
    }

    private static RateLimitPartition<string> PartitionByIp(
        HttpContext context, RateLimitOptions options, RateLimitRule rule, string policy)
    {
        var ip = ClientIp(context);
        return IsExempt(ip, options)
            ? RateLimitPartition.GetNoLimiter($"{policy}:exempt")
            : Fixed($"{policy}:ip:{ip}", rule);
    }

    private static RateLimitPartition<string> PartitionByUser(
        HttpContext context, RateLimitOptions options, RateLimitRule rule, string policy)
    {
        var ip = ClientIp(context);
        if (IsExempt(ip, options))
            return RateLimitPartition.GetNoLimiter($"{policy}:exempt");

        // Per user when we know who they are, per IP when we don't. Partitioning an authenticated
        // request by IP instead would put a whole office behind one NAT address into a single bucket.
        var userId = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrEmpty(userId)
            ? Fixed($"{policy}:ip:{ip}", rule)
            : Fixed($"{policy}:user:{userId}", rule);
    }

    private static RateLimitPartition<string> Fixed(string key, RateLimitRule rule) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rule.PermitLimit,
            Window = TimeSpan.FromSeconds(rule.WindowSeconds),
            QueueLimit = rule.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

    private static bool IsExempt(string ip, RateLimitOptions options) =>
        options.ExemptIpAddresses.Length > 0 &&
        options.ExemptIpAddresses.Contains(ip, StringComparer.OrdinalIgnoreCase);

    /// <summary>The caller's IP as the app sees it. <c>UseForwardedHeaders</c> runs before the
    /// limiter in Program.cs, so behind the ngrok tunnel or any reverse proxy this is already the
    /// real client rather than the proxy - which is what makes a per-IP partition mean anything.
    /// "unknown" for a connection with no remote IP (in-process test host), which buckets all such
    /// callers together; that is correct, since they are all the same caller.</summary>
    private static string ClientIp(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return "unknown";

        // An IPv4 client reaching a dual-stack socket arrives as ::ffff:203.0.113.5, which would
        // otherwise partition separately from the same client seen as plain IPv4 elsewhere.
        return remote.IsIPv4MappedToIPv6
            ? remote.MapToIPv4().ToString()
            : remote.ToString();
    }
}
