using System.Net;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Api.Health;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Infrastructure.Notifications;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Authentication Delivery page: SMTP, MSG91 and the web app address, saved to the AppSettings store only with the secrets
/// never read back, and what is saved is what the senders bind from.</summary>
public class PlatformDeliverySettingsTests
{
    private sealed class MemoryStore : IAppSettingsStore
    {
        public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

        public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
                Rows[key] = value;
            return Task.CompletedTask;
        }

        public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeTester : IDeliveryTester
    {
        public DeliveryEmailSettings? Email { get; private set; }
        public DeliverySmsSettings? Sms { get; private set; }
        public string? To { get; private set; }

        public Task<DeliveryTestResultDto> SendEmailAsync(DeliveryEmailSettings settings, string toEmail, CancellationToken cancellationToken = default)
        {
            Email = settings;
            To = toEmail;
            return Task.FromResult(new DeliveryTestResultDto(true, "sent"));
        }

        public Task<DeliveryTestResultDto> SendSmsAsync(DeliverySmsSettings settings, string toPhoneE164, CancellationToken cancellationToken = default)
        {
            Sms = settings;
            To = toPhoneE164;
            return Task.FromResult(new DeliveryTestResultDto(true, "sent"));
        }
    }

    private static PlatformDeliverySettingsService Service(MemoryStore store, FakeTester? tester = null) =>
        new(store, new UpdatePlatformDeliverySettingsRequestValidator(), tester ?? new FakeTester());

    private static UpdatePlatformDeliverySettingsRequest Request(
        string publicUrl = "https://app.example.com/", string host = " smtp.example.com ", int port = 465, string user = "mailer",
        string from = "no-reply@example.com", bool ssl = true, string? password = "smtp-pass-1234",
        bool smsEnabled = true, string template = "tpl-otp-1", string baseUrl = "", string? authKey = "msg91-key-9876") =>
        new(publicUrl, host, port, user, from, ssl, password, smsEnabled, template, baseUrl, authKey);

    [Fact]
    public async Task Nothing_stored_reads_as_unconfigured_with_sensible_defaults()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.False(dto.IsEmailConfigured);
        Assert.False(dto.IsSmsConfigured);
        Assert.Equal(587, dto.SmtpPort);
        Assert.True(dto.SmtpEnableSsl);
        Assert.Equal(PlatformDeliverySettingsService.DefaultSmsBaseUrl, dto.SmsBaseUrl);
        Assert.False(dto.HasSmtpPassword);
        Assert.False(dto.HasSmsAuthKey);
    }

    [Fact]
    public async Task Saved_values_are_trimmed_and_the_secrets_come_back_masked()
    {
        var store = new MemoryStore();

        var dto = await Service(store).UpdateAsync(Request(), Guid.NewGuid());

        Assert.Equal("https://app.example.com", dto.PublicUrl);
        Assert.Equal("smtp.example.com", dto.SmtpHost);
        Assert.True(dto.IsEmailConfigured);
        Assert.True(dto.IsSmsConfigured);
        Assert.Equal("••••1234", dto.SmtpPasswordHint);
        Assert.Equal("••••9876", dto.SmsAuthKeyHint);
        Assert.Equal("smtp-pass-1234", store.Rows[PlatformDeliverySettingsService.SmtpPasswordKey]);
    }

    [Fact]
    public async Task A_null_secret_keeps_the_stored_one_and_an_empty_one_clears_it()
    {
        var store = new MemoryStore();
        var service = Service(store);
        await service.UpdateAsync(Request(), null);

        var kept = await service.UpdateAsync(Request(host: "smtp2.example.com", password: null, authKey: null), null);
        Assert.True(kept.HasSmtpPassword);
        Assert.True(kept.HasSmsAuthKey);

        var cleared = await service.UpdateAsync(Request(user: "", password: "", smsEnabled: false, authKey: ""), null);
        Assert.False(cleared.HasSmtpPassword);
        Assert.False(cleared.HasSmsAuthKey);
    }

    [Fact]
    public async Task Turning_sms_on_needs_an_auth_key_and_a_template()
    {
        var service = Service(new MemoryStore());

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(Request(authKey: ""), null));
        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(Request(template: ""), null));
    }

    [Fact]
    public async Task An_smtp_user_without_a_password_is_refused()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service(new MemoryStore()).UpdateAsync(Request(password: ""), null));
    }

    [Theory]
    [InlineData("not a url", "no-reply@example.com", "")]
    [InlineData("https://app.example.com", "not-an-email", "")]
    [InlineData("https://app.example.com", "no-reply@example.com", "http://insecure.example.com/")]
    public async Task Malformed_values_are_refused(string publicUrl, string from, string baseUrl)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Service(new MemoryStore()).UpdateAsync(Request(publicUrl: publicUrl, from: from, baseUrl: baseUrl), null));
    }

    [Fact]
    public async Task What_is_saved_is_what_the_senders_bind_from()
    {
        var store = new MemoryStore();
        await Service(store).UpdateAsync(Request(), null);
        var config = new ConfigurationBuilder().AddInMemoryCollection(store.Rows).Build();

        var smtp = config.GetSection("Email:Smtp").Get<SmtpOptions>()!;
        var sms = config.GetSection("Sms:Msg91").Get<Msg91Options>()!;
        var app = config.GetSection("App").Get<AppLinkOptions>()!;

        Assert.True(smtp.IsConfigured);
        Assert.Equal(465, smtp.Port);
        Assert.Equal("smtp-pass-1234", smtp.Password);
        Assert.True(sms.IsConfigured);
        Assert.Equal("msg91-key-9876", sms.AuthKey);
        Assert.Equal("https://app.example.com", app.PublicUrl);
    }

    [Fact]
    public async Task A_test_uses_the_form_values_and_falls_back_to_the_stored_secret()
    {
        var store = new MemoryStore();
        var tester = new FakeTester();
        var service = Service(store, tester);
        await service.UpdateAsync(Request(), null);

        await service.TestEmailAsync(new SendDeliveryTestRequest(Request(host: "other.example.com", password: null), "me@example.com"));
        Assert.Equal("other.example.com", tester.Email!.Host);
        Assert.Equal("smtp-pass-1234", tester.Email.Password);
        Assert.Equal("me@example.com", tester.To);

        await service.TestSmsAsync(new SendDeliveryTestRequest(Request(authKey: null), "+91 98765 43210"));
        Assert.Equal("msg91-key-9876", tester.Sms!.AuthKey);
        Assert.Equal("+919876543210", tester.To);
    }

    [Fact]
    public async Task A_test_text_needs_a_number_with_a_country_code()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service(new MemoryStore()).TestSmsAsync(new SendDeliveryTestRequest(Request(), "98765 43210")));
    }

    [Fact]
    public void The_keys_are_in_the_settings_catalog_and_the_secrets_are_flagged()
    {
        foreach (var key in new[]
                 {
                     PlatformDeliverySettingsService.PublicUrlKey, PlatformDeliverySettingsService.SmtpHostKey, PlatformDeliverySettingsService.SmtpPortKey,
                     PlatformDeliverySettingsService.SmtpUserKey, PlatformDeliverySettingsService.SmtpPasswordKey, PlatformDeliverySettingsService.SmtpFromKey,
                     PlatformDeliverySettingsService.SmtpEnableSslKey, PlatformDeliverySettingsService.SmsEnabledKey, PlatformDeliverySettingsService.SmsAuthKeyKey,
                     PlatformDeliverySettingsService.SmsTemplateKey, PlatformDeliverySettingsService.SmsBaseUrlKey,
                 })
            Assert.Contains(AppSettingCatalog.All, d => d.Key == key);

        Assert.True(AppSettingCatalog.All.Single(d => d.Key == PlatformDeliverySettingsService.SmtpPasswordKey).IsSecret);
        Assert.True(AppSettingCatalog.All.Single(d => d.Key == PlatformDeliverySettingsService.SmsAuthKeyKey).IsSecret);
    }
}

/// <summary>The MSG91 call: our code, the template, the number as digits, and the key kept out of the URL.</summary>
public class Msg91ClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{\"type\":\"success\",\"message\":\"abc123\"}";
        public bool Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (Throw)
                throw new HttpRequestException("network down");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }

    private static Msg91Options Options(bool enabled = true) => new()
    {
        Enabled = enabled, AuthKey = "secret-key", OtpTemplateId = "tpl-1", BaseUrl = "https://sms.example.test/api/v5"
    };

    private static Task<DeliveryResult> Send(StubHandler handler, Msg91Options options) =>
        Msg91Client.SendOtpAsync(new HttpClient(handler), options, "+91 98765-43210".Replace(" ", "").Replace("-", ""), "482913", NullLogger.Instance);

    [Fact]
    public async Task The_request_carries_the_template_the_digits_and_our_code_with_the_key_in_a_header()
    {
        var handler = new StubHandler();

        var result = await Send(handler, Options());

        Assert.True(result.Success);
        var url = handler.Request!.RequestUri!.ToString();
        Assert.StartsWith("https://sms.example.test/api/v5/otp?", url);
        Assert.Contains("template_id=tpl-1", url);
        Assert.Contains("mobile=919876543210", url);
        Assert.Contains("otp=482913", url);
        Assert.DoesNotContain("secret-key", url);
        Assert.Equal("secret-key", handler.Request.Headers.GetValues("authkey").Single());
    }

    [Fact]
    public async Task A_provider_error_is_a_failure_with_the_reason()
    {
        var handler = new StubHandler { Body = "{\"type\":\"error\",\"message\":\"Template not found\"}" };

        var result = await Send(handler, Options());

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Contains("Template not found", result.Note);
    }

    [Fact]
    public async Task A_server_error_and_a_network_failure_are_failures_not_exceptions()
    {
        var failing = await Send(new StubHandler { Status = HttpStatusCode.InternalServerError, Body = "oops" }, Options());
        var down = await Send(new StubHandler { Throw = true }, Options());

        Assert.False(failing.Success);
        Assert.False(down.Success);
    }

    [Fact]
    public async Task When_sms_is_off_or_unconfigured_nothing_is_called_and_the_send_is_skipped()
    {
        var handler = new StubHandler();

        var off = await Send(handler, Options(enabled: false));
        var noKey = await Send(handler, new Msg91Options { Enabled = true });

        Assert.True(off.Skipped);
        Assert.True(noKey.Skipped);
        Assert.Null(handler.Request);
    }
}

public class SmtpMailerTests
{
    [Fact]
    public async Task With_no_host_the_send_is_skipped_not_failed()
    {
        var result = await SmtpMailer.SendAsync(new SmtpOptions(), "a@example.com", "Hi", "Body", NullLogger.Instance);

        Assert.True(result.Skipped);
        Assert.False(result.Success);
    }
}

public sealed class DatabaseHealthCheckTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_reachable_database_is_healthy()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using var db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task An_unreachable_database_is_unhealthy_without_leaking_why()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=/no-such-directory/none.db;Mode=ReadOnly").Options;
        using var db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain("no-such-directory", result.Description ?? string.Empty);
    }
}
