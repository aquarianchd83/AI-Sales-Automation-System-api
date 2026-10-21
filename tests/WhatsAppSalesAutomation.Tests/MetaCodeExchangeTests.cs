using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.MetaOnboarding;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The one-time code from Meta's popup is exchanged for the tenant's token. Meta ties the code to the redirect address it was
/// issued for, so "redirect_uri mismatch" (code 100, subcode 36008) is retried with the forms Meta is reported to accept; any other
/// answer ends the attempts at once. The page the popup was opened from is logged, without its query string.</summary>
public sealed class MetaCodeExchangeTests : IDisposable
{
    private const string Mismatch = """{"error":{"message":"Error validating verification code. Please make sure your redirect_uri is identical to the one you used in the OAuth dialog request","type":"OAuthException","code":100,"error_subcode":36008,"fbtrace_id":"T1"}}""";
    private const string Rejected = """{"error":{"message":"Invalid OAuth access token - Cannot parse access token","type":"OAuthException","code":190,"fbtrace_id":"T2"}}""";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly ScriptedMeta _meta = new();
    private readonly CapturingLogger _log = new();
    private readonly MetaEmbeddedSignupService _service;

    public MetaCodeExchangeTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant), new AnonymousUser()) { StampTenantId = _tenant };
        _db.Database.EnsureCreated();

        var protection = new EphemeralDataProtectionProvider();
        var clock = new TestClock();
        var provider = new TenantWhatsAppConfigProvider(_db, new Ambient(_tenant), protection, clock, new UpdateTenantWhatsAppConfigRequestValidator());
        var settings = new WhatsAppSettings { AppId = "app-1", AppSecret = "app-secret", EmbeddedSignupConfigId = "cfg-1", ApiVersion = "v21.0", ApiBaseUrl = "https://graph.facebook.com/" };
        _service = new MetaEmbeddedSignupService(new HttpClient(_meta), _db, provider, protection, clock, new Snapshot(settings), _log);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<MetaSignupResultDto> CompleteAsync(string? pageUrl) =>
        _service.CompleteAsync(_tenant, null, new CompleteMetaSignupRequest("the-code", null, null, "FINISH", null, null, pageUrl));

    private List<string> Exchanges => _meta.Requests.Where(u => u.Contains("oauth/access_token") && !u.Contains("client_credentials")).ToList();

    private List<string> Probes => _meta.Requests.Where(u => u.Contains("grant_type=client_credentials")).ToList();

    [Fact]
    public async Task Meta_documented_form_is_tried_first_and_a_code_it_accepts_is_not_retried()
    {
        _meta.When("oauth/access_token", HttpStatusCode.OK, """{"access_token":"EAAG-new"}""");
        _meta.When("debug_token", HttpStatusCode.BadRequest, Rejected); // stop right after the exchange

        await CompleteAsync("https://app.example/tenant-settings");

        var exchanges = Exchanges;
        Assert.Single(exchanges);
        Assert.DoesNotContain("redirect_uri", exchanges[0]);
        Assert.Contains("code=the-code", exchanges[0]);
    }

    [Fact]
    public async Task A_redirect_uri_mismatch_is_retried_with_an_empty_redirect_uri_and_stops_when_that_works()
    {
        _meta.WhenSequence("oauth/access_token", (HttpStatusCode.BadRequest, Mismatch), (HttpStatusCode.OK, """{"access_token":"EAAG-new"}"""));
        _meta.When("debug_token", HttpStatusCode.BadRequest, Rejected);

        await CompleteAsync("https://app.example/tenant-settings");

        var exchanges = Exchanges;
        Assert.Equal(2, exchanges.Count);
        Assert.DoesNotContain("redirect_uri", exchanges[0]);
        Assert.EndsWith("&redirect_uri=", exchanges[1]);
        Assert.Contains(_log.Messages, m => m.Contains("succeeded with redirect_uri variant empty"));
    }

    [Fact]
    public async Task A_mismatch_that_persists_tries_the_page_then_its_origin_and_then_reports_it()
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Mismatch);

        var result = await CompleteAsync("https://app.example/tenant-settings?a=1#fragment");

        var exchanges = Exchanges;
        Assert.Equal(4, exchanges.Count);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://app.example/tenant-settings?a=1"), exchanges[2]);
        Assert.DoesNotContain("fragment", exchanges[2]);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://app.example"), exchanges[3]);
        Assert.NotNull(result.Issue);
        Assert.Equal(MetaSignupSteps.Authorization, result.Issue!.Step);
    }

    [Fact]
    public async Task When_every_form_is_refused_the_platform_app_credentials_are_checked_and_found_good()
    {
        _meta.When("grant_type=client_credentials", HttpStatusCode.OK, """{"access_token":"app-token","token_type":"bearer"}""");
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Mismatch);

        await CompleteAsync("https://app.example/tenant-settings");

        Assert.Single(Probes);
        Assert.Contains(_log.Messages, m => m.Contains("Meta accepts the platform app id and secret") && m.Contains("about the code itself"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("app-token") || m.Contains("app-secret"));
    }

    [Fact]
    public async Task When_every_form_is_refused_a_rejected_platform_secret_is_named_as_the_cause()
    {
        _meta.When("grant_type=client_credentials", HttpStatusCode.BadRequest, """{"error":{"message":"Error validating client secret.","type":"OAuthException","code":1,"fbtrace_id":"T3"}}""");
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Mismatch);

        await CompleteAsync("https://app.example/tenant-settings");

        Assert.Contains(_log.Messages, m => m.Contains("Meta REJECTS the platform app id and secret") && m.Contains("WhatsApp:AppSecret"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("app-secret"));
    }

    [Fact]
    public async Task The_credentials_are_not_checked_when_the_exchange_fails_for_another_reason_or_succeeds()
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Rejected);
        await CompleteAsync("https://app.example/tenant-settings");
        Assert.Empty(Probes);
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("1098765", "2233445", true, true)]
    public async Task Whether_the_popups_finished_message_arrived_is_logged_without_the_ids(string? phone, string? waba, bool hasPhone, bool hasWaba)
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Rejected);

        await _service.CompleteAsync(_tenant, null, new CompleteMetaSignupRequest("the-code", waba, phone, null, null, null, "https://app.example/tenant-settings"));

        Assert.Contains(_log.Messages, m => m.Contains($"code returned True, business account id reported {hasWaba}, phone number id reported {hasPhone}"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("2233445") || m.Contains("1098765") || m.Contains("the-code"));
    }

    [Fact]
    public async Task Without_a_page_address_only_the_none_and_empty_forms_are_tried()
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Mismatch);

        await CompleteAsync(null);

        Assert.Equal(2, Exchanges.Count);
        Assert.Contains(_log.Messages, m => m.Contains("popup opened from (not reported)"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://app.example/x")]
    [InlineData("https://user:secret@app.example/x")]
    public async Task A_page_address_that_is_not_plain_http_or_https_is_never_sent_to_Meta_or_logged(string pageUrl)
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Mismatch);

        await CompleteAsync(pageUrl);

        Assert.Equal(2, Exchanges.Count);
        Assert.All(Exchanges, u => Assert.DoesNotContain("alert", u));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("user:secret") || m.Contains("alert"));
    }

    [Fact]
    public async Task Any_other_refusal_ends_the_attempts_at_once()
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Rejected);

        var result = await CompleteAsync("https://app.example/tenant-settings");

        Assert.Single(Exchanges);
        Assert.NotNull(result.Issue);
    }

    [Fact]
    public async Task The_page_address_is_logged_without_its_query_string_or_fragment_and_the_code_never_is()
    {
        _meta.When("oauth/access_token", HttpStatusCode.BadRequest, Rejected);

        await CompleteAsync("https://app.example/tenant-settings?token=abc123#frag");

        Assert.Contains(_log.Messages, m => m.Contains("popup opened from https://app.example/tenant-settings,"));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("abc123") || m.Contains("frag") || m.Contains("the-code") || m.Contains("app-secret"));
    }

    // ---- fakes ----

    private sealed class ScriptedMeta : HttpMessageHandler
    {
        private readonly List<(string Fragment, Queue<(HttpStatusCode Status, string Body)> Replies, (HttpStatusCode Status, string Body) Repeat)> _rules = new();

        public List<string> Requests { get; } = new();

        public void When(string fragment, HttpStatusCode status, string body) =>
            _rules.Add((fragment, new Queue<(HttpStatusCode, string)>(), (status, body)));

        /// <summary>The replies in order; the last one repeats.</summary>
        public void WhenSequence(string fragment, params (HttpStatusCode Status, string Body)[] replies) =>
            _rules.Add((fragment, new Queue<(HttpStatusCode, string)>(replies.Take(replies.Length - 1)), replies[^1]));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            foreach (var (fragment, queue, repeat) in _rules)
            {
                if (!uri.Contains(fragment))
                    continue;
                var (status, body) = queue.Count > 0 ? queue.Dequeue() : repeat;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class CapturingLogger : ILogger<MetaEmbeddedSignupService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Snapshot : IOptionsSnapshot<WhatsAppSettings>
    {
        public Snapshot(WhatsAppSettings value) => Value = value;
        public WhatsAppSettings Value { get; }
        public WhatsAppSettings Get(string? name) => Value;
    }

    private sealed class Ambient : ITenantContext
    {
        public Ambient(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; private set; }
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => TenantId = tenantId;
    }
}
