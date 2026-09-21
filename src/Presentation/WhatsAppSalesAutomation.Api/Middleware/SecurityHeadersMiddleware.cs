using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Api.Middleware;

/// <summary>
/// Stamps the response security headers described by <see cref="SecurityHeaderOptions"/>.
///
/// Uses <c>OnStarting</c> rather than writing the headers before calling the next middleware, because
/// static file serving and the Hangfire dashboard both set their own headers and a plain up-front
/// write can be overwritten by them. <c>OnStarting</c> runs at the last possible moment before the
/// response begins, so these values win - which is the point of a security header.
///
/// Reads <see cref="IOptionsMonitor{T}"/>, not <see cref="IOptionsSnapshot{T}"/>: this is registered
/// as middleware (effectively a singleton), so a scoped snapshot cannot be injected into it.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SecurityHeaderOptions> _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptionsMonitor<SecurityHeaderOptions> options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return _next(context);

        context.Response.OnStarting(static state =>
        {
            var (ctx, opts) = ((HttpContext, SecurityHeaderOptions))state;
            Apply(ctx, opts);
            return Task.CompletedTask;
        }, (context, options));

        return _next(context);
    }

    private static void Apply(HttpContext context, SecurityHeaderOptions options)
    {
        var headers = context.Response.Headers;

        // The one header with no downside and a real exploit behind it: without it, a browser may
        // sniff a JSON response that happens to start with HTML-looking bytes and execute it.
        headers["X-Content-Type-Options"] = "nosniff";

        // Superseded by CSP's frame-ancestors for current browsers, sent for older ones.
        headers["X-Frame-Options"] = "DENY";

        if (!string.IsNullOrWhiteSpace(options.ContentSecurityPolicy))
            headers["Content-Security-Policy"] = options.ContentSecurityPolicy;

        if (!string.IsNullOrWhiteSpace(options.ReferrerPolicy))
            headers["Referrer-Policy"] = options.ReferrerPolicy;

        if (!string.IsNullOrWhiteSpace(options.PermissionsPolicy))
            headers["Permissions-Policy"] = options.PermissionsPolicy;

        // Only meaningful over HTTPS, and a browser ignores it on a plain-HTTP response anyway - but
        // the check also keeps it off the loopback responses behind the ngrok tunnel described in
        // Program.cs, where the request arrives as HTTP with X-Forwarded-Proto set.
        if (options.EnableHsts && context.Request.IsHttps)
        {
            var value = $"max-age={(int)TimeSpan.FromDays(options.HstsMaxAgeDays).TotalSeconds}";
            if (options.HstsIncludeSubDomains)
                value += "; includeSubDomains";
            if (options.HstsPreload)
                value += "; preload";
            headers["Strict-Transport-Security"] = value;
        }

        if (options.RemoveServerHeaders)
        {
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
        }
    }
}
