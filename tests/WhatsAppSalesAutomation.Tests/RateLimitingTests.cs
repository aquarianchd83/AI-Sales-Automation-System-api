using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WhatsAppSalesAutomation.Api.Extensions;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// Drives the real <c>AddRateLimiting</c> wiring over a minimal TestServer rather than the whole app,
/// so the limiter is exercised end-to-end (policy resolution, partitioning, the OnRejected body)
/// without booting migrations against a live SQL Server.
///
/// The cases that matter are the ones a hand-check would not catch: that the named policies exist at
/// all (a typo in a <c>[EnableRateLimiting]</c> name throws only when that endpoint is first hit),
/// that the exemption list is honoured, and that "Enabled: false" really is a no-op rather than a
/// startup failure - the last one because every policy still has to be registered for the attributes
/// to resolve.
/// </summary>
public class RateLimitingTests
{
    private static async Task<IHost> StartHostAsync(Dictionary<string, string?> settings)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddRateLimiting(context.Configuration);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/auth", () => "ok")
                            .RequireRateLimiting(RateLimitingServiceExtensions.AuthPolicy);
                        endpoints.MapGet("/webhook", () => "ok")
                            .RequireRateLimiting(RateLimitingServiceExtensions.WebhookPolicy);
                        endpoints.MapGet("/api", () => "ok")
                            .RequireRateLimiting(RateLimitingServiceExtensions.ApiPolicy);
                    });
                }))
            .StartAsync();

        return host;
    }

    private static Dictionary<string, string?> Settings(
        bool enabled = true, int authLimit = 3, string? exemptIp = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = enabled.ToString(),
            ["RateLimiting:Auth:PermitLimit"] = authLimit.ToString(),
            ["RateLimiting:Auth:WindowSeconds"] = "60",
            ["RateLimiting:Webhook:PermitLimit"] = "600",
            ["RateLimiting:Webhook:WindowSeconds"] = "60",
            ["RateLimiting:Api:PermitLimit"] = "300",
            ["RateLimiting:Api:WindowSeconds"] = "60"
        };

        if (exemptIp is not null)
            settings["RateLimiting:ExemptIpAddresses:0"] = exemptIp;

        return settings;
    }

    /// <summary>TestServer leaves RemoteIpAddress null unless something sets it, and "unknown" is a
    /// single partition - fine for the limit tests, but the exemption test needs a real value.</summary>
    private static HttpClient ClientWithIp(IHost host, IPAddress? ip)
    {
        var server = host.GetTestServer();
        if (ip is not null)
            server.BaseAddress = new Uri("http://localhost/");

        return server.CreateClient();
    }

    [Fact]
    public async Task Auth_policy_rejects_past_its_limit_with_retry_after()
    {
        using var host = await StartHostAsync(Settings(authLimit: 3));
        var client = ClientWithIp(host, null);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth")).StatusCode);

        var rejected = await client.GetAsync("/auth");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        // Without this a client retries immediately and just burns the next window too.
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Contains("retryAfterSeconds", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Policies_are_bucketed_separately()
    {
        using var host = await StartHostAsync(Settings(authLimit: 1));
        var client = ClientWithIp(host, null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/auth")).StatusCode);

        // Exhausting the auth budget must not cost the webhook endpoint anything - the whole reason
        // these are three buckets and not one is that a login flood should never drop a Meta delivery.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/webhook")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api")).StatusCode);
    }

    [Fact]
    public async Task Disabled_registers_the_policies_but_never_rejects()
    {
        // The failure this guards against is subtle: dropping the AddPolicy calls when disabled makes
        // the app start and then throw on the first request to a [EnableRateLimiting("auth")] action.
        using var host = await StartHostAsync(Settings(enabled: false, authLimit: 1));
        var client = ClientWithIp(host, null);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth")).StatusCode);
    }

    [Fact]
    public async Task Exempt_ip_is_never_limited()
    {
        using var host = await StartHostAsync(Settings(authLimit: 1, exemptIp: "203.0.113.7"));
        var server = host.GetTestServer();

        // CreateClient gives no control over RemoteIpAddress, so drive the pipeline directly.
        for (var i = 0; i < 5; i++)
        {
            var context = await server.SendAsync(ctx =>
            {
                ctx.Request.Method = HttpMethods.Get;
                ctx.Request.Path = "/auth";
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            });

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task A_non_exempt_ip_is_still_limited()
    {
        using var host = await StartHostAsync(Settings(authLimit: 1, exemptIp: "203.0.113.7"));
        var server = host.GetTestServer();

        var first = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = "/auth";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        });
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);

        var second = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = "/auth";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        });
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
    }

    [Fact]
    public async Task Ipv4_mapped_and_plain_ipv4_share_one_partition()
    {
        // ::ffff:198.51.100.4 and 198.51.100.4 are the same caller. Partitioning them separately
        // would silently double every per-IP budget on a dual-stack socket.
        using var host = await StartHostAsync(Settings(authLimit: 1));
        var server = host.GetTestServer();

        var first = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = "/auth";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        });
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);

        var second = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = "/auth";
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4").MapToIPv6();
        });
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
    }
}
