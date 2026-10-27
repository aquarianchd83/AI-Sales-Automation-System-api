using System.Text;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Application.Webhooks;
using WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The bridge's public webhook: who may call it, which tenant a call belongs to, and what is (and is not) stored.</summary>
public class WhatsAppBridgeWebhookControllerTests
{
    private const string Token = "path-token";
    private const string Secret = "header-secret";
    private const string InstanceToken = "instance-token-xyz";
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly FakeChannels _channels = new();
    private readonly FakeChannelService _service = new();
    private readonly FakeProcessor _processor = new();
    private readonly FakeJobs _jobs = new();

    private async Task<IActionResult> Post(string body, string token = Token, string? secret = Secret)
    {
        var controller = new WhatsAppBridgeWebhooksController(_channels, _service, _processor, _jobs, NullLogger<WhatsAppBridgeWebhooksController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (secret is not null)
            http.Request.Headers[WhatsAppChannelService.SecretHeaderName] = secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return await controller.Receive(token, CancellationToken.None);
    }

    private static string Message(string id = "AC1", bool fromMe = false, string jid = "919800000001@s.whatsapp.net", string apiKey = InstanceToken, string instance = "t-mine") =>
        $$"""{"event":"messages.upsert","instance":"{{instance}}","data":{"key":{"remoteJid":"{{jid}}","fromMe":{{(fromMe ? "true" : "false")}},"id":"{{id}}"},"pushName":"Asha","message":{"conversation":"Hello"},"messageType":"conversation","messageTimestamp":1793071801},"apikey":"{{apiKey}}"}""";

    [Fact]
    public async Task A_wrong_secret_is_rejected_and_nothing_is_recorded()
    {
        Assert.IsType<UnauthorizedResult>(await Post(Message(), secret: "wrong"));

        Assert.Empty(_processor.Recorded);
        Assert.Equal(0, _jobs.Enqueued);
    }

    [Fact]
    public async Task A_missing_secret_is_rejected()
    {
        Assert.IsType<UnauthorizedResult>(await Post(Message(), secret: null));
        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task An_unknown_channel_token_is_rejected()
    {
        Assert.IsType<UnauthorizedResult>(await Post(Message(), token: "someone-elses"));
        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task A_delivery_whose_body_token_is_not_this_channels_instance_token_is_rejected()
    {
        Assert.IsType<UnauthorizedResult>(await Post(Message(apiKey: "another-instances-token")));

        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task A_delivery_with_no_body_token_is_rejected()
    {
        Assert.IsType<UnauthorizedResult>(await Post("""{"event":"messages.upsert","data":{}}"""));
    }

    [Fact]
    public async Task A_customer_message_is_recorded_for_the_tenant_the_channel_names_never_one_the_payload_claims()
    {
        // The payload names a different instance; the tenant still comes only from the authenticated channel.
        var result = await Post(Message(instance: "t-ffffffffffffffffffffffffffffffff"));

        Assert.IsType<OkResult>(result);
        var recorded = Assert.Single(_processor.Recorded);
        Assert.Equal(Tenant, recorded.TenantId);
        Assert.Equal("whatsapp_bridge_webhook", recorded.EventType);
        Assert.Equal(1, _jobs.Enqueued);
    }

    [Fact]
    public async Task What_is_stored_never_contains_the_instance_token()
    {
        await Post(Message());

        var stored = Assert.Single(_processor.Recorded).Payload;
        Assert.DoesNotContain(InstanceToken, stored);
        Assert.DoesNotContain("apikey", stored);
        Assert.Contains("AC1", stored);
    }

    [Fact]
    public async Task A_message_typed_on_the_linked_phone_is_acknowledged_but_not_recorded()
    {
        Assert.IsType<OkResult>(await Post(Message(fromMe: true)));

        Assert.Empty(_processor.Recorded);
        Assert.Equal(0, _jobs.Enqueued);
    }

    [Fact]
    public async Task A_group_message_is_acknowledged_but_not_recorded()
    {
        Assert.IsType<OkResult>(await Post(Message(jid: "120363000000000001@g.us")));

        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task A_qr_event_is_acknowledged_and_nothing_about_it_is_kept()
    {
        Assert.IsType<OkResult>(await Post($$$"""{"event":"qrcode.updated","data":{"qrcode":{"base64":"data:image/png;base64,AAA"}},"apikey":"{{{InstanceToken}}}"}"""));

        Assert.Empty(_processor.Recorded);
        Assert.Empty(_service.Updates);
    }

    [Fact]
    public async Task A_connection_update_changes_the_channel_status_and_is_not_stored_as_a_message()
    {
        Assert.IsType<OkResult>(await Post($$"""{"event":"connection.update","data":{"state":"open","wuid":"919800000009@s.whatsapp.net"},"apikey":"{{InstanceToken}}"}"""));

        var update = Assert.Single(_service.Updates);
        Assert.Equal((Tenant, "open", "919800000009"), update);
        Assert.Empty(_processor.Recorded);
    }

    [Fact]
    public async Task A_delivery_status_is_recorded_as_a_status_for_the_authenticated_tenant()
    {
        await Post($$"""{"event":"messages.update","data":{"keyId":"3EB05","remoteJid":"1111111111111@lid","fromMe":true,"status":"DELIVERY_ACK"},"apikey":"{{InstanceToken}}"}""");

        var recorded = Assert.Single(_processor.Recorded);
        Assert.Equal(Tenant, recorded.TenantId);
        Assert.Contains("3EB05", recorded.Payload);
        Assert.Contains("delivered", recorded.Payload);
    }

    // ---- fakes ----------------------------------------------------------------------------------------------------

    private sealed class FakeChannels : IWhatsAppChannelStore
    {
        public Task<WhatsAppChannelRecord?> FindByWebhookAsync(string token, string secret, CancellationToken ct = default) =>
            Task.FromResult<WhatsAppChannelRecord?>(token == Token && secret == Secret
                ? new WhatsAppChannelRecord(Tenant, "t-mine", InstanceToken, BridgeStatuses.Connected, null, null, DateTime.UtcNow, DateTime.UtcNow)
                : null);

        public Task<WhatsAppChannelRecord?> GetAsync(Guid tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelWebhookCredentials> UpsertAsync(Guid t, string n, string i, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(Guid t, string s, string? p, string? e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeChannelService : IWhatsAppChannelService
    {
        public List<(Guid Tenant, string? State, string? Phone)> Updates { get; } = new();

        public Task ApplyConnectionUpdateAsync(Guid tenantId, string? providerState, string? connectedPhone, CancellationToken ct = default)
        {
            Updates.Add((tenantId, providerState, connectedPhone));
            return Task.CompletedTask;
        }

        public Task<WhatsAppChannelStatusDto> GetStatusAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelStatusDto> ConnectAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelStatusDto> DisconnectAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelStatusDto> RemoveAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeProcessor : IInboundWebhookProcessor
    {
        public List<(Guid TenantId, string EventType, string Payload)> Recorded { get; } = new();

        public Task<Guid> RecordAsync(Guid tenantId, string eventType, string rawPayload, CancellationToken ct = default)
        {
            Recorded.Add((tenantId, eventType, rawPayload));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<WebhookProcessOutcome> ProcessAsync(Guid tenantId, Guid webhookEventId, int attempt = 1, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeJobs : IBackgroundJobClient
    {
        public int Enqueued { get; private set; }

        public string Create(Job job, IState state)
        {
            Enqueued++;
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}

public class WhatsAppBridgeHealthJobTests
{
    [Fact]
    public async Task One_tenants_failure_does_not_stop_the_pass_and_every_channel_is_checked()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = new Store(a, b);
        var service = new Service { Throws = a };
        var job = new WhatsAppBridgeHealthJob(store, service, NullLogger<WhatsAppBridgeHealthJob>.Instance);

        await job.RunAsync();

        Assert.Equal(new[] { a, b }, service.Checked);
    }

    [Fact]
    public async Task With_no_channels_nothing_is_called()
    {
        var service = new Service();

        await new WhatsAppBridgeHealthJob(new Store(), service, NullLogger<WhatsAppBridgeHealthJob>.Instance).RunAsync();

        Assert.Empty(service.Checked);
    }

    private sealed class Store : IWhatsAppChannelStore
    {
        private readonly Guid[] _ids;
        public Store(params Guid[] ids) => _ids = ids;
        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(_ids);
        public Task<WhatsAppChannelRecord?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<WhatsAppChannelRecord?>(new WhatsAppChannelRecord(tenantId, "t", "tok", BridgeStatuses.Connected, null, null, DateTime.UtcNow, DateTime.UtcNow));
        public Task<WhatsAppChannelRecord?> FindByWebhookAsync(string t, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelWebhookCredentials> UpsertAsync(Guid t, string n, string i, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(Guid t, string s, string? p, string? e, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class Service : IWhatsAppChannelService
    {
        public Guid? Throws { get; set; }
        public List<Guid> Checked { get; } = new();

        public Task<WhatsAppChannelStatusDto> GetStatusAsync(Guid tenantId, CancellationToken ct = default)
        {
            Checked.Add(tenantId);
            if (tenantId == Throws)
                throw new InvalidOperationException("boom");
            return Task.FromResult(new WhatsAppChannelStatusDto(BridgeStatuses.Connected, null, null, null, null, null));
        }

        public Task<WhatsAppChannelStatusDto> ConnectAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelStatusDto> DisconnectAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WhatsAppChannelStatusDto> RemoveAsync(Guid t, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApplyConnectionUpdateAsync(Guid t, string? s, string? p, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
