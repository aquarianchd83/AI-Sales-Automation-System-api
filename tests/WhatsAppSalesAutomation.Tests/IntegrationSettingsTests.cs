using System.Net;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Settings;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Integrations;
using WhatsAppSalesAutomation.Infrastructure.Sms;
using WhatsAppSalesAutomation.Infrastructure.Verification;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Integrations page: per outside service (consent SMS, DND check), simulator or third-party API, saved to the AppSettings
/// store only with the key never read back, and what is saved is what the routing providers bind from.</summary>
public class PlatformIntegrationSettingsTests
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

    private sealed class FakeTester : IIntegrationTester
    {
        public IntegrationApiSettings? Sms { get; private set; }
        public IntegrationApiSettings? Dnd { get; private set; }

        public Task<DeliveryTestResultDto> SendConsentSmsAsync(IntegrationApiSettings settings, string toPhoneE164, CancellationToken cancellationToken = default)
        {
            Sms = settings;
            return Task.FromResult(new DeliveryTestResultDto(true, "sent"));
        }

        public Task<DeliveryTestResultDto> CheckDndAsync(IntegrationApiSettings settings, string phoneE164, CancellationToken cancellationToken = default)
        {
            Dnd = settings;
            return Task.FromResult(new DeliveryTestResultDto(true, "checked"));
        }
    }

    private static PlatformIntegrationSettingsService Service(MemoryStore store, FakeTester? tester = null) =>
        new(store, new UpdatePlatformIntegrationSettingsRequestValidator(), tester ?? new FakeTester());

    private static IntegrationChannelRequest Simulator() => new("Simulator", "", null, "Authorization", "", 15);

    private static IntegrationChannelRequest Api(string url = "https://sms.example.com/send", string? key = "sms-key-1234", string header = "Authorization", string sender = "SALESX") =>
        new("Api", url, key, header, sender, 20);

    [Fact]
    public async Task Nothing_stored_means_both_channels_use_the_simulator()
    {
        var dto = await Service(new MemoryStore()).GetAsync();

        Assert.Equal("Simulator", dto.ConsentSms.Mode);
        Assert.Equal("Simulator", dto.DndCheck.Mode);
        Assert.False(dto.ConsentSms.IsLive);
        Assert.False(dto.DndCheck.HasApiKey);
        Assert.Equal(PlatformIntegrationSettingsService.DefaultTimeoutSeconds, dto.ConsentSms.TimeoutSeconds);
    }

    [Fact]
    public async Task Each_channel_is_switched_on_its_own_and_the_key_is_never_read_back()
    {
        var store = new MemoryStore();
        var dto = await Service(store).UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Api(), Simulator()), null);

        Assert.Equal("Api", dto.ConsentSms.Mode);
        Assert.True(dto.ConsentSms.IsLive);
        Assert.True(dto.ConsentSms.HasApiKey);
        Assert.Equal("••••1234", dto.ConsentSms.ApiKeyHint);
        Assert.Equal("Simulator", dto.DndCheck.Mode);
        Assert.False(dto.DndCheck.IsLive);
        Assert.DoesNotContain("sms-key-1234", System.Text.Json.JsonSerializer.Serialize(dto));
        Assert.Equal("sms-key-1234", store.Rows["Integrations:ConsentSms:ApiKey"]);
    }

    [Fact]
    public async Task A_blank_key_keeps_the_stored_one_and_an_empty_one_clears_it()
    {
        var store = new MemoryStore();
        var service = Service(store);
        await service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Api(), Simulator()), null);

        await service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Api(key: null, sender: "NEWID"), Simulator()), null);
        Assert.Equal("sms-key-1234", store.Rows["Integrations:ConsentSms:ApiKey"]);
        Assert.Equal("NEWID", store.Rows["Integrations:ConsentSms:SenderId"]);

        var cleared = await service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Simulator() with { ApiKey = "" }, Simulator()), null);
        Assert.False(cleared.ConsentSms.HasApiKey);
    }

    [Fact]
    public async Task The_api_cannot_be_switched_on_without_a_key_or_with_a_bad_address()
    {
        var service = Service(new MemoryStore());

        var noKey = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Api(key: null), Simulator()), null));
        Assert.Contains(noKey.Errors, e => e.PropertyName == "ConsentSms.ApiKey");

        var http = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Simulator(), Api(url: "http://dnd.example.com")), null));
        Assert.Contains(http.Errors, e => e.PropertyName == "DndCheck.ApiUrl");

        var header = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Api(header: "Bad Header\r\n"), Simulator()), null));
        Assert.Contains(header.Errors, e => e.PropertyName == "ConsentSms.ApiKeyHeader");
    }

    [Fact]
    public async Task What_is_saved_is_what_the_routing_providers_bind_from()
    {
        var store = new MemoryStore();
        await Service(store).UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Api(), Simulator()), null);
        var config = new ConfigurationBuilder().AddInMemoryCollection(store.Rows).Build();

        var sms = config.GetSection("Integrations:ConsentSms").Get<ConsentSmsIntegrationOptions>()!;
        var dnd = config.GetSection("Integrations:DndCheck").Get<DndCheckIntegrationOptions>()!;

        Assert.True(sms.UsesApi);
        Assert.Equal("https://sms.example.com/send", sms.ApiUrl);
        Assert.Equal("sms-key-1234", sms.ApiKey);
        Assert.Equal(20, sms.TimeoutSeconds);
        Assert.False(dnd.UsesApi);
    }

    [Fact]
    public async Task A_test_in_simulator_mode_sends_nothing_and_a_test_in_api_mode_uses_the_stored_key_when_the_field_is_blank()
    {
        var store = new MemoryStore();
        var tester = new FakeTester();
        var service = Service(store, tester);

        var simulated = await service.TestConsentSmsAsync(new SendIntegrationTestRequest(Simulator(), "+919876543210"));
        Assert.True(simulated.Success);
        Assert.Null(tester.Sms);

        await service.UpdateAsync(new UpdatePlatformIntegrationSettingsRequest(Simulator(), Api(url: "https://dnd.example.com/check", key: "dnd-key-5678")), null);
        await service.TestDndCheckAsync(new SendIntegrationTestRequest(Api(url: "https://dnd.example.com/check", key: null), "+919876543210"));
        Assert.Equal("dnd-key-5678", tester.Dnd!.ApiKey);

        await Assert.ThrowsAsync<ValidationException>(() => service.TestConsentSmsAsync(new SendIntegrationTestRequest(Api(key: null), "+919876543210")));
        await Assert.ThrowsAsync<ValidationException>(() => service.TestConsentSmsAsync(new SendIntegrationTestRequest(Api(), "not a number")));
    }

    [Fact]
    public void The_integration_keys_are_declared_and_the_keys_are_secret()
    {
        Assert.Contains("Integrations", AppSettingCatalog.Categories);
        foreach (var prefix in new[] { "Integrations:ConsentSms", "Integrations:DndCheck" })
        {
            Assert.Contains(AppSettingCatalog.All, d => d.Key == prefix + ":Mode");
            Assert.True(AppSettingCatalog.All.Single(d => d.Key == prefix + ":ApiKey").IsSecret);
        }
    }
}

public class ThirdPartyIntegrationTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Throw)
                throw new HttpRequestException("network down");
            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static IntegrationApiSettings Settings(string header = "Authorization") =>
        new("https://provider.example.com/api", "secret-key", header, "SALESX", 5);

    private static readonly ConsentSmsRequest Sms = new("+919876543210", "Reply Hi to chat", "key-1");

    [Fact]
    public async Task The_sms_request_carries_the_number_text_reference_and_sender_with_the_key_as_a_bearer_token()
    {
        var handler = new StubHandler { Body = "{\"messageId\":\"m-42\"}" };

        var result = await ThirdPartyConsentSmsClient.SendAsync(new HttpClient(handler), Settings(), Sms, NullLogger.Instance);

        Assert.True(result.Accepted);
        Assert.Equal("m-42", result.ProviderMessageId);
        Assert.Null(result.ImmediateReceipt);   // a real provider reports delivery later, by a receipt
        Assert.Equal("Bearer secret-key", handler.Request!.Headers.GetValues("Authorization").Single());
        using var sent = System.Text.Json.JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("+919876543210", sent.RootElement.GetProperty("to").GetString());
        Assert.Equal("Reply Hi to chat", sent.RootElement.GetProperty("message").GetString());
        Assert.Equal("key-1", sent.RootElement.GetProperty("reference").GetString());
        Assert.Equal("SALESX", sent.RootElement.GetProperty("senderId").GetString());
    }

    [Fact]
    public async Task A_custom_key_header_carries_the_key_as_is()
    {
        var handler = new StubHandler { Body = "{\"id\":\"x\"}" };

        await ThirdPartyConsentSmsClient.SendAsync(new HttpClient(handler), Settings("X-Api-Key"), Sms, NullLogger.Instance);

        Assert.Equal("secret-key", handler.Request!.Headers.GetValues("X-Api-Key").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    public async Task A_rejection_is_permanent_unless_the_provider_is_down_or_busy(HttpStatusCode status, bool transient)
    {
        var handler = new StubHandler { Status = status, Body = "{\"message\":\"Number invalid\"}" };

        var result = await ThirdPartyConsentSmsClient.SendAsync(new HttpClient(handler), Settings(), Sms, NullLogger.Instance);

        Assert.False(result.Accepted);
        Assert.Equal("Number invalid", result.FailureReason);
        Assert.Equal(transient, result.Transient);
    }

    [Fact]
    public async Task An_unreachable_sms_provider_is_a_transient_failure()
    {
        var result = await ThirdPartyConsentSmsClient.SendAsync(new HttpClient(new StubHandler { Throw = true }), Settings(), Sms, NullLogger.Instance);

        Assert.False(result.Accepted);
        Assert.True(result.Transient);
    }

    [Fact]
    public async Task Dnd_answers_are_matched_by_reference_and_a_missing_answer_is_an_error_never_a_pass()
    {
        var pass = new PhoneVerificationItem(Guid.NewGuid(), "+919800000001");
        var fail = new PhoneVerificationItem(Guid.NewGuid(), "+919800000002");
        var silent = new PhoneVerificationItem(Guid.NewGuid(), "+919800000003");
        var handler = new StubHandler
        {
            // Out of order on purpose: matching is by reference, not position.
            Body = $"{{\"requestId\":\"r-1\",\"results\":[{{\"reference\":\"{fail.ReferenceId}\",\"status\":\"fail\",\"reasonCode\":\"DND\",\"reason\":\"On the register\"}},{{\"reference\":\"{pass.ReferenceId}\",\"status\":\"pass\"}}]}}"
        };

        var response = await ThirdPartyDndClient.VerifyAsync(new HttpClient(handler), Settings(), new[] { pass, fail, silent }, NullLogger.Instance);

        Assert.Equal("r-1", response.RequestId);
        Assert.Equal(LeadDiscoveryVerificationStatus.Pass, response.Results.Single(r => r.ReferenceId == pass.ReferenceId).Status);
        var failed = response.Results.Single(r => r.ReferenceId == fail.ReferenceId);
        Assert.Equal(LeadDiscoveryVerificationStatus.Fail, failed.Status);
        Assert.Equal("DND", failed.ReasonCode);
        Assert.Equal(LeadDiscoveryVerificationStatus.Error, response.Results.Single(r => r.ReferenceId == silent.ReferenceId).Status);
    }

    [Fact]
    public async Task A_failed_dnd_call_marks_every_number_as_an_error()
    {
        var items = new[] { new PhoneVerificationItem(Guid.NewGuid(), "+919800000001"), new PhoneVerificationItem(Guid.NewGuid(), "+919800000002") };

        var down = await ThirdPartyDndClient.VerifyAsync(new HttpClient(new StubHandler { Throw = true }), Settings(), items, NullLogger.Instance);
        var rejected = await ThirdPartyDndClient.VerifyAsync(new HttpClient(new StubHandler { Status = HttpStatusCode.Unauthorized, Body = "{\"error\":\"bad key\"}" }), Settings(), items, NullLogger.Instance);

        Assert.All(down.Results, r => Assert.Equal(LeadDiscoveryVerificationStatus.Error, r.Status));
        Assert.All(rejected.Results, r => Assert.Equal(LeadDiscoveryVerificationStatus.Error, r.Status));
        Assert.All(rejected.Results, r => Assert.Equal("bad key", r.Reason));
    }

    [Fact]
    public async Task The_routing_providers_follow_the_mode_and_default_to_the_simulator()
    {
        var handler = new StubHandler { Body = "{\"messageId\":\"m-1\"}" };
        var factory = new StubFactory(handler);
        var smsOptions = new ConsentSmsIntegrationOptions();
        var sender = new RoutingConsentSmsSender(new FixedSnapshot<ConsentSmsIntegrationOptions>(smsOptions),
            new SimulatedConsentSmsSender(NullLogger<SimulatedConsentSmsSender>.Instance), factory, NullLogger<RoutingConsentSmsSender>.Instance);

        var simulated = await sender.SendAsync(Sms);
        Assert.True(simulated.Accepted);
        Assert.NotNull(simulated.ImmediateReceipt);   // the simulator answers in the same call
        Assert.Null(handler.Request);                 // nothing went out

        smsOptions.Mode = "Api";
        smsOptions.ApiUrl = "https://provider.example.com/api";
        smsOptions.ApiKey = "secret-key";
        var live = await sender.SendAsync(Sms);
        Assert.Equal("m-1", live.ProviderMessageId);
        Assert.NotNull(handler.Request);

        var dndOptions = new DndCheckIntegrationOptions();
        var provider = new RoutingPhoneVerificationProvider(new FixedSnapshot<DndCheckIntegrationOptions>(dndOptions),
            new SimulatedPhoneVerificationProvider(), factory, NullLogger<RoutingPhoneVerificationProvider>.Instance);
        handler.Body = "{\"results\":[]}";
        var item = new PhoneVerificationItem(Guid.NewGuid(), "+919800000001");

        var viaSimulator = await provider.VerifyAsync(Guid.NewGuid(), new[] { item });
        Assert.StartsWith("sim-", viaSimulator.RequestId);

        dndOptions.Mode = "Api";
        dndOptions.ApiUrl = "https://provider.example.com/dnd";
        dndOptions.ApiKey = "dnd-key";
        var viaApi = await provider.VerifyAsync(Guid.NewGuid(), new[] { item });
        Assert.StartsWith("dnd-", viaApi.RequestId);
        Assert.Equal(LeadDiscoveryVerificationStatus.Error, viaApi.Results.Single().Status);   // the stub gave no answer for it
    }

    private sealed class FixedSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public FixedSnapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
