using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Api.Middleware;
using WhatsAppSalesAutomation.Application.Common.Options;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Pins the response headers and, more importantly, the two conditions that decide whether HSTS is
/// sent at all. Getting HSTS wrong is the one mistake here with a long tail: a browser that receives
/// it from a dev box refuses plain HTTP to that host for months, and nothing in the app can take it
/// back.
///
/// Runs over a TestServer rather than a bare <c>DefaultHttpContext</c> because the middleware writes
/// its headers from an <c>OnStarting</c> callback - see its own doc comment for why. DefaultHttpContext
/// never invokes those callbacks, so a test built on one passes every "header is absent" assertion
/// whether the middleware works or not, which is the wrong half to be confident about.
/// </summary>
public class SecurityHeadersMiddlewareTests
{
    private static async Task<HttpResponseMessage> GetAsync(SecurityHeaderOptions options, bool https = true)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                    services.AddSingleton<IOptionsMonitor<SecurityHeaderOptions>>(
                        new StaticOptionsMonitor<SecurityHeaderOptions>(options)))
                .Configure(app =>
                {
                    app.UseMiddleware<SecurityHeadersMiddleware>();

                    // Something downstream must set a header the middleware also sets, so the test
                    // proves OnStarting wins rather than merely that nothing contested it. This is
                    // what static file serving and the Hangfire dashboard do in the real pipeline.
                    app.Run(context =>
                    {
                        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                        context.Response.Headers["Server"] = "Kestrel";
                        return context.Response.WriteAsync("ok");
                    });
                }))
            .StartAsync();

        var client = host.GetTestServer().CreateClient();
        host.GetTestServer().BaseAddress = new Uri(https ? "https://localhost/" : "http://localhost/");

        return await host.GetTestServer().CreateClient().GetAsync("/");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    [Fact]
    public async Task Sends_the_baseline_headers()
    {
        var response = await GetAsync(new SecurityHeaderOptions());

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Contains("frame-ancestors 'none'", Header(response, "Content-Security-Policy")!);
        Assert.Contains("object-src 'none'", Header(response, "Content-Security-Policy")!);
        Assert.Contains("camera=()", Header(response, "Permissions-Policy")!);
    }

    [Fact]
    public async Task Overwrites_a_weaker_header_set_downstream()
    {
        // The terminal delegate sets SAMEORIGIN. DENY winning is the entire reason the middleware
        // uses OnStarting instead of writing before calling next().
        var response = await GetAsync(new SecurityHeaderOptions());

        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task Strips_the_server_banner()
    {
        var response = await GetAsync(new SecurityHeaderOptions());

        Assert.Null(Header(response, "Server"));
    }

    [Fact]
    public async Task Disabled_sends_nothing_and_leaves_downstream_headers_alone()
    {
        var response = await GetAsync(new SecurityHeaderOptions { Enabled = false });

        Assert.Null(Header(response, "X-Content-Type-Options"));
        Assert.Null(Header(response, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Equal("Kestrel", Header(response, "Server"));
    }

    [Fact]
    public async Task Hsts_is_absent_by_default()
    {
        var response = await GetAsync(new SecurityHeaderOptions());

        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_carries_max_age_and_directives_when_enabled()
    {
        var response = await GetAsync(new SecurityHeaderOptions
        {
            EnableHsts = true,
            HstsMaxAgeDays = 180,
            HstsIncludeSubDomains = true,
            HstsPreload = true
        });

        Assert.Equal("max-age=15552000; includeSubDomains; preload", Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_is_withheld_from_a_plain_http_request()
    {
        // The ngrok/reverse-proxy setup in Program.cs forwards plain HTTP to Kestrel, and a browser
        // would ignore the header on such a response anyway - but sending it would also mean a
        // developer hitting http://localhost gets their browser pinned for six months.
        var response = await GetAsync(new SecurityHeaderOptions { EnableHsts = true }, https: false);

        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Empty_policy_strings_suppress_their_headers()
    {
        var response = await GetAsync(new SecurityHeaderOptions
        {
            ContentSecurityPolicy = "",
            ReferrerPolicy = "",
            PermissionsPolicy = ""
        });

        Assert.Null(Header(response, "Content-Security-Policy"));
        Assert.Null(Header(response, "Referrer-Policy"));
        Assert.Null(Header(response, "Permissions-Policy"));
        // The two unconditional ones survive - they have no configuration knob on purpose.
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
