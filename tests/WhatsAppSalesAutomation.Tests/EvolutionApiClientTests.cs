using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The Evolution client against a fake server: the exact requests it sends and, above all, which failures are safe to retry.</summary>
public class EvolutionApiClientTests
{
    private static readonly BridgeEndpoint Endpoint = new("http://bridge:8085/", "the-key");
    private readonly FakeHandler _handler = new();

    private EvolutionApiClient Client() => new(new HttpClient(_handler), NullLogger<EvolutionApiClient>.Instance);

    [Fact]
    public async Task A_send_posts_flat_number_and_text_with_the_key_and_returns_the_provider_message_id()
    {
        _handler.Reply(HttpStatusCode.Created, """{"key":{"remoteJid":"919800000001@s.whatsapp.net","fromMe":true,"id":"3EB053"},"status":"PENDING"}""");

        var result = await Client().SendTextAsync(Endpoint, "t-1", "+91 98000 00001", "hello");

        Assert.True(result.Success);
        Assert.Equal("3EB053", result.WhatsAppMessageId);
        Assert.Equal(WhatsAppProviders.QrBridge, result.Provider);
        Assert.False(result.Ambiguous);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("http://bridge:8085/message/sendText/t-1", request.Uri);
        Assert.Equal("the-key", request.ApiKey);
        // Flat body, digits only - the nested textMessage form was rejected by the real server.
        Assert.Equal("""{"number":"919800000001","text":"hello"}""", request.Body);
    }

    [Fact]
    public async Task A_refusal_is_a_plain_failure_that_is_safe_to_retry()
    {
        _handler.Reply(HttpStatusCode.BadRequest, """{"status":400}""");

        var result = await Client().SendTextAsync(Endpoint, "t-1", "+919800000001", "hello");

        Assert.False(result.Success);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public async Task A_server_error_may_have_sent_the_message_so_it_is_not_safe_to_retry()
    {
        _handler.Reply(HttpStatusCode.InternalServerError, "{}");

        Assert.True((await Client().SendTextAsync(Endpoint, "t-1", "+919800000001", "hello")).Ambiguous);
    }

    [Fact]
    public async Task A_timeout_is_ambiguous()
    {
        _handler.Throw(new TaskCanceledException("timeout"));

        var result = await Client().SendTextAsync(Endpoint, "t-1", "+919800000001", "hello");

        Assert.False(result.Success);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task A_connection_dropped_mid_reply_is_ambiguous()
    {
        _handler.Throw(new HttpRequestException("reset"));

        Assert.True((await Client().SendTextAsync(Endpoint, "t-1", "+919800000001", "hello")).Ambiguous);
    }

    [Fact]
    public async Task A_refused_connection_provably_sent_nothing_so_it_is_safe_to_retry()
    {
        _handler.Throw(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));

        var result = await Client().SendTextAsync(Endpoint, "t-1", "+919800000001", "hello");

        Assert.False(result.Success);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public async Task An_accepted_reply_with_no_message_id_is_ambiguous_rather_than_a_clean_failure()
    {
        _handler.Reply(HttpStatusCode.Created, """{"status":"PENDING"}""");

        var result = await Client().SendTextAsync(Endpoint, "t-1", "+919800000001", "hello");

        Assert.False(result.Success);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task Create_returns_the_instance_token_and_the_first_qr()
    {
        _handler.Reply(HttpStatusCode.Created,
            """{"instance":{"instanceName":"t-1","status":"connecting"},"hash":"inst-token","qrcode":{"pairingCode":null,"code":"2@abc","base64":"data:image/png;base64,AAA","count":1}}""");

        var created = await Client().CreateInstanceAsync(Endpoint, "t-1");

        Assert.Equal("inst-token", created.InstanceToken);
        Assert.Equal("data:image/png;base64,AAA", created.Qr!.Base64);
        Assert.Equal("""{"instanceName":"t-1","integration":"WHATSAPP-BAILEYS","qrcode":true}""", _handler.Requests[0].Body);
    }

    [Fact]
    public async Task A_missing_instance_has_no_state_rather_than_an_error()
    {
        _handler.Reply(HttpStatusCode.NotFound, """{"status":404}""");

        Assert.Null(await Client().GetStateAsync(Endpoint, "t-1"));
    }

    [Fact]
    public async Task The_webhook_is_registered_with_the_secret_header_and_only_the_events_the_adapter_uses()
    {
        _handler.Reply(HttpStatusCode.Created, "{}");

        await Client().ConfigureWebhookAsync(Endpoint, "t-1", "https://api/x", "X-Bridge-Secret", "s3cret");

        var body = _handler.Requests[0].Body;
        Assert.Contains("\"X-Bridge-Secret\":\"s3cret\"", body);
        Assert.Contains("\"url\":\"https://api/x\"", body);
        Assert.Contains("CONNECTION_UPDATE", body);
        Assert.Contains("MESSAGES_UPSERT", body);
        Assert.DoesNotContain("QRCODE_UPDATED", body);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_without_echoing_it()
    {
        _handler.Reply(HttpStatusCode.Unauthorized, "{}");

        var ex = await Assert.ThrowsAsync<WhatsAppBridgeException>(() => Client().PingAsync(Endpoint));

        Assert.DoesNotContain("the-key", ex.Message);
    }

    [Fact]
    public async Task Deleting_an_instance_that_is_already_gone_is_not_an_error()
    {
        _handler.Reply(HttpStatusCode.NotFound, "{}");

        await Client().DeleteAsync(Endpoint, "t-1");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        private Exception? _throw;

        public List<(string Uri, string? ApiKey, string? Body)> Requests { get; } = new();

        public void Reply(HttpStatusCode status, string body) { _status = status; _body = body; _throw = null; }
        public void Throw(Exception ex) => _throw = ex;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("apikey", out var v) ? v.First() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_throw is not null)
                throw _throw;
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
