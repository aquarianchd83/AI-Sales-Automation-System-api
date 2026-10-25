using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Api.Extensions;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Constants;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The customer-facing endpoints: anonymous and rate-limited, GET reads only, and every outcome maps to the right status.</summary>
public sealed class PublicConsentControllerTests
{
    private sealed class FakeConsentPages : IConsentPageService
    {
        public PublicConsentPageDto View { get; set; } = new(PublicConsentStates.Ready, null, null, null);
        public ConsentDecisionResultDto Decision { get; set; } = new(ConsentDecisionOutcomes.Recorded, "Granted", "ok");
        public int Decisions { get; private set; }
        public ConsentClientInfo? Client { get; private set; }

        public Task<PublicConsentPageDto> GetAsync(string token, CancellationToken cancellationToken = default) => Task.FromResult(View);

        public Task<ConsentDecisionResultDto> DecideAsync(string token, ConsentDecisionRequest request, ConsentClientInfo client, CancellationToken cancellationToken = default)
        {
            Decisions++;
            Client = client;
            return Task.FromResult(Decision);
        }
    }

    private static PublicConsentController Controller(FakeConsentPages pages)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "TestBrowser/1.0";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        return new PublicConsentController(pages) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public void The_customer_endpoints_are_anonymous_and_rate_limited_and_the_settings_endpoints_are_admin_only()
    {
        var customer = typeof(PublicConsentController);
        Assert.NotNull(customer.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(RateLimitingServiceExtensions.ConsentPagePolicy, customer.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);

        var settings = typeof(ConsentPageSettingsController);
        Assert.Null(settings.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(AppRoles.Admin, settings.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
    }

    [Fact]
    public void Only_the_decision_action_can_change_anything_and_it_is_a_post()
    {
        var get = typeof(PublicConsentController).GetMethod(nameof(PublicConsentController.Get))!;
        var decide = typeof(PublicConsentController).GetMethod(nameof(PublicConsentController.Decide))!;

        Assert.NotNull(get.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(get.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(decide.GetCustomAttribute<HttpPostAttribute>());
    }

    [Fact]
    public async Task Viewing_a_link_never_reaches_the_decision_path()
    {
        var pages = new FakeConsentPages();

        var result = await Controller(pages).Get("token", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(0, pages.Decisions);
    }

    [Theory]
    [InlineData(PublicConsentStates.Invalid, StatusCodes.Status404NotFound)]
    [InlineData(PublicConsentStates.Expired, StatusCodes.Status410Gone)]
    [InlineData(PublicConsentStates.Unavailable, StatusCodes.Status200OK)]
    [InlineData(PublicConsentStates.Answered, StatusCodes.Status200OK)]
    public async Task A_view_maps_each_state_to_its_status(string state, int status)
    {
        var pages = new FakeConsentPages { View = new PublicConsentPageDto(state, null, null, "m") };

        var result = await Controller(pages).Get("token", CancellationToken.None);

        Assert.Equal(status, ((ObjectResult)result.Result!).StatusCode);
    }

    [Theory]
    [InlineData(ConsentDecisionOutcomes.Recorded, StatusCodes.Status200OK)]
    [InlineData(ConsentDecisionOutcomes.AlreadyRecorded, StatusCodes.Status200OK)]
    [InlineData(ConsentDecisionOutcomes.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(ConsentDecisionOutcomes.Invalid, StatusCodes.Status404NotFound)]
    [InlineData(ConsentDecisionOutcomes.Expired, StatusCodes.Status410Gone)]
    [InlineData(ConsentDecisionOutcomes.NotAllowed, StatusCodes.Status403Forbidden)]
    [InlineData(ConsentDecisionOutcomes.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ConsentDecisionOutcomes.PageChanged, StatusCodes.Status409Conflict)]
    [InlineData(ConsentDecisionOutcomes.Unavailable, StatusCodes.Status409Conflict)]
    public async Task A_decision_maps_each_outcome_to_its_status(string outcome, int status)
    {
        var pages = new FakeConsentPages { Decision = new ConsentDecisionResultDto(outcome, null, "m") };

        var result = await Controller(pages).Decide("token", new ConsentDecisionRequest("agree", true, 1), CancellationToken.None);

        Assert.Equal(status, ((ObjectResult)result.Result!).StatusCode);
    }

    [Fact]
    public async Task The_clients_address_and_browser_come_from_the_connection_not_from_the_body_and_responses_are_not_cached()
    {
        var pages = new FakeConsentPages();
        var controller = Controller(pages);

        await controller.Decide("token", new ConsentDecisionRequest("agree", true, 1), CancellationToken.None);

        Assert.Equal("203.0.113.7", pages.Client!.IpAddress);
        Assert.Equal("TestBrowser/1.0", pages.Client.UserAgent);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
    }
}

/// <summary>The demo SMS outbox: only with the setting on, only for the signed-in tenant, and it hands back the link to click.</summary>
public sealed class DemoSmsOutboxTests
{
    private sealed class Tenant : WhatsAppSalesAutomation.Application.Common.Interfaces.ITenantContext
    {
        public Tenant(Guid? id) => TenantId = id;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static (DemoSmsOutboxController Controller, WhatsAppSalesAutomation.Infrastructure.Sms.SimulatedSmsOutbox Outbox) Make(bool enabled, Guid? tenant)
    {
        var outbox = new WhatsAppSalesAutomation.Infrastructure.Sms.SimulatedSmsOutbox();
        var controller = new DemoSmsOutboxController(outbox, new Tenant(tenant), Microsoft.Extensions.Options.Options.Create(new ConsentPageOptions { DemoOutboxEnabled = enabled }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, outbox);
    }

    [Fact]
    public void It_is_off_unless_the_setting_is_on()
    {
        var (controller, outbox) = Make(enabled: false, A);
        outbox.Add(A, "+919800000001", "Hi! https://app.example.com/consent/abc", DateTime.UtcNow);

        Assert.IsType<NotFoundResult>(controller.Get().Result);
    }

    [Fact]
    public void It_lists_only_the_signed_in_tenants_messages_newest_first_with_the_link_and_its_path()
    {
        var (controller, outbox) = Make(enabled: true, A);
        outbox.Add(A, "+919800000001", "first https://app.example.com/consent/tok1", new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc));
        outbox.Add(A, "+919800000002", "second https://app.example.com/consent/tok2", new DateTime(2026, 10, 25, 2, 0, 0, DateTimeKind.Utc));
        outbox.Add(B, "+919800000003", "other tenant https://app.example.com/consent/tok3", DateTime.UtcNow);

        var items = Assert.IsAssignableFrom<IReadOnlyList<DemoSmsOutboxController.DemoSmsDto>>(((OkObjectResult)controller.Get().Result!).Value);

        Assert.Equal(new[] { "+919800000002", "+919800000001" }, items.Select(i => i.To));
        Assert.Equal("https://app.example.com/consent/tok2", items[0].Link);
        Assert.Equal("/consent/tok2", items[0].Path);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task The_simulator_keeps_messages_only_when_the_demo_setting_is_on()
    {
        var outbox = new WhatsAppSalesAutomation.Infrastructure.Sms.SimulatedSmsOutbox();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<WhatsAppSalesAutomation.Infrastructure.Sms.SimulatedConsentSmsSender>.Instance;
        var request = new WhatsAppSalesAutomation.Application.Common.Interfaces.ConsentSmsRequest("+919800000001", "Hi https://app.example.com/consent/t", "k");

        await new WhatsAppSalesAutomation.Infrastructure.Sms.SimulatedConsentSmsSender(log, outbox, new Tenant(A), new TestClock(),
            Microsoft.Extensions.Options.Options.Create(new ConsentPageOptions { DemoOutboxEnabled = false })).SendAsync(request);
        Assert.Empty(outbox.List(A));

        await new WhatsAppSalesAutomation.Infrastructure.Sms.SimulatedConsentSmsSender(log, outbox, new Tenant(A), new TestClock(),
            Microsoft.Extensions.Options.Options.Create(new ConsentPageOptions { DemoOutboxEnabled = true })).SendAsync(request);
        Assert.Single(outbox.List(A));
    }
}
