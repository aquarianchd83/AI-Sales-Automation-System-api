using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Infrastructure.Payments;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// The backend reaching Razorpay through its MCP server: JSON-RPC over Streamable HTTP, the merchant token as Basic auth, one session per key,
/// Razorpay's JSON read out of the tool's text content - and a switch back to the REST API.
/// </summary>
public sealed class RazorpayMcpGatewayTests
{
    private static readonly RazorpayCredentials Keys = new("rzp_test_KEY", "secret");

    private readonly FakeMcpServer _server = new();
    private readonly RestStub _restHandler = new();
    private readonly MemorySettingsStore _settings = new();

    private RazorpayMcpGateway Gateway() => new(
        new RazorpayMcpClient(new HttpClient(_server), NullLogger<RazorpayMcpClient>.Instance),
        new RazorpayGateway(new HttpClient(_restHandler) { BaseAddress = new Uri("https://api.razorpay.test/v1/") }, NullLogger<RazorpayGateway>.Instance),
        _settings);

    [Fact]
    public async Task An_order_is_created_through_the_rest_api_because_razorpays_mcp_server_has_no_create_order_tool()
    {
        _restHandler.Body = """{"id":"order_R1","amount":59000,"currency":"INR","receipt":"wr-1","status":"created"}""";

        var result = await Gateway().CreateOrderAsync(Keys, 59_000, "INR", "wr-1", new Dictionary<string, string> { ["source"] = "wallet-recharge" });

        Assert.True(result.Success);
        Assert.Equal("order_R1", result.Value!.Id);
        Assert.Equal(1, _restHandler.Calls);
        Assert.Equal("POST", _restHandler.Requests.Single().Method);
        Assert.EndsWith("/orders", _restHandler.Requests.Single().Path);
        Assert.Empty(_server.Calls);
        Assert.Equal(0, _server.Initializes);
    }

    [Fact]
    public async Task Every_request_carries_the_merchant_token_and_the_session_is_opened_once()
    {
        _server.Tools["fetch_payment"] = args => Payment(args["payment_id"]!.GetValue<string>(), "captured");
        var gateway = Gateway();

        await gateway.FetchPaymentAsync(Keys, "pay_1");
        await gateway.FetchPaymentAsync(Keys, "pay_2");

        Assert.Equal(1, _server.Initializes);
        Assert.All(_server.Authorizations, a => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("rzp_test_KEY:secret")), a));
        Assert.All(_server.Calls, c => Assert.Equal(FakeMcpServer.SessionId, c.Session));
    }

    [Fact]
    public async Task A_payment_is_read_with_fetch_payment_including_a_reply_sent_as_an_event_stream()
    {
        _server.UseEventStream = true;
        _server.Tools["fetch_payment"] = _ => Payment("pay_1", "authorized");

        var result = await Gateway().FetchPaymentAsync(Keys, "pay_1");

        Assert.True(result.Success);
        Assert.Equal("authorized", result.Value!.Status);
        Assert.Equal("order_1", result.Value.OrderId);
        Assert.Equal(59_000, result.Value.Amount);
        Assert.Equal("upi", result.Value.Method);
    }

    [Fact]
    public async Task The_payments_of_an_order_come_from_fetch_order_payments()
    {
        _server.Tools["fetch_order_payments"] = _ => new JsonObject
        {
            ["entity"] = "collection", ["count"] = 2, ["items"] = new JsonArray(Payment("pay_1", "failed"), Payment("pay_2", "captured"))
        };

        var result = await Gateway().FetchOrderPaymentsAsync(Keys, "order_1");

        Assert.True(result.Success);
        Assert.Equal(new[] { "failed", "captured" }, result.Value!.Select(p => p.Status));
        Assert.Equal("order_1", _server.Calls.Single().Arguments["order_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Capture_and_refund_go_through_the_rest_api()
    {
        _restHandler.Body = Payment("pay_1", "captured").ToJsonString();
        var gateway = Gateway();

        var captured = await gateway.CapturePaymentAsync(Keys, "pay_1", 59_000, "INR");
        _restHandler.Body = """{"id":"rfnd_1","payment_id":"pay_1","amount":59000,"status":"processed"}""";
        var refunded = await gateway.RefundAsync(Keys, "pay_1", 59_000);

        Assert.Equal("captured", captured.Value!.Status);
        Assert.Equal("rfnd_1", refunded.Value!.Id);
        Assert.Equal(2, _restHandler.Calls);
        Assert.Contains(_restHandler.Requests, r => r.Path.EndsWith("/payments/pay_1/capture"));
        Assert.Contains(_restHandler.Requests, r => r.Path.EndsWith("/payments/pay_1/refund"));
        Assert.Empty(_server.Calls);
    }

    [Fact]
    public async Task A_read_whose_tool_the_mcp_server_does_not_have_is_answered_by_the_rest_api()
    {
        // No fetch_payment tool on this server: it answers "tool 'fetch_payment' not found: tool not found".
        _restHandler.Body = Payment("pay_1", "captured").ToJsonString();

        var result = await Gateway().FetchPaymentAsync(Keys, "pay_1");

        Assert.True(result.Success);
        Assert.Equal("pay_1", result.Value!.Id);
        Assert.Single(_server.Calls);
        Assert.Equal(1, _restHandler.Calls);
    }

    [Fact]
    public async Task A_tool_error_comes_back_as_a_failed_result_in_razorpays_words()
    {
        _server.Tools["fetch_payment"] = _ => throw new ToolError("fetching payment failed: The id provided does not exist");

        var result = await Gateway().FetchPaymentAsync(Keys, "pay_missing");

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Error);
    }

    [Fact]
    public async Task Keys_the_mcp_server_refuses_are_reported_not_thrown()
    {
        _server.RefuseAuth = true;

        var result = await Gateway().PingAsync(Keys);

        Assert.False(result.Success);
        Assert.Contains("API keys", result.Error);
    }

    [Fact]
    public async Task The_key_check_uses_fetch_all_orders()
    {
        _server.Tools["fetch_all_orders"] = _ => new JsonObject { ["entity"] = "collection", ["count"] = 0, ["items"] = new JsonArray() };

        Assert.True((await Gateway().PingAsync(Keys)).Success);
        Assert.Equal(1, _server.Calls.Single().Arguments["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_forgotten_session_is_opened_again_and_the_call_retried()
    {
        _server.Tools["fetch_payment"] = _ => Payment("pay_1", "captured");
        var gateway = Gateway();
        await gateway.FetchPaymentAsync(Keys, "pay_1");

        _server.ForgetSessions();
        var result = await gateway.FetchPaymentAsync(Keys, "pay_1");

        Assert.True(result.Success);
        Assert.Equal(2, _server.Initializes);
    }

    [Fact]
    public async Task Transport_rest_sends_everything_straight_to_the_rest_api()
    {
        _settings.Rows[RazorpayMcpGateway.TransportKey] = "Rest";
        _restHandler.Body = Payment("pay_1", "captured").ToJsonString();

        var result = await Gateway().FetchPaymentAsync(Keys, "pay_1");

        Assert.True(result.Success);
        Assert.Equal(1, _restHandler.Calls);
        Assert.Empty(_server.Calls);
        Assert.Equal(0, _server.Initializes);
    }

    [Fact]
    public async Task Subscriptions_have_no_mcp_tool_and_always_use_the_rest_api()
    {
        _restHandler.Body = """{"id":"sub_1","plan_id":"plan_1","status":"active"}""";

        var result = await Gateway().FetchSubscriptionAsync(Keys, "sub_1");

        Assert.True(result.Success);
        Assert.Equal(1, _restHandler.Calls);
        Assert.Equal(0, _server.Initializes);
    }

    [Fact]
    public async Task A_custom_mcp_url_is_used_when_set()
    {
        _settings.Rows[RazorpayMcpGateway.McpUrlKey] = "https://mcp.example.test/mcp";
        _server.Tools["fetch_payment"] = _ => Payment("pay_1", "captured");

        await Gateway().FetchPaymentAsync(Keys, "pay_1");

        Assert.All(_server.Urls, u => Assert.Equal("https://mcp.example.test/mcp", u));
    }

    private static JsonObject Payment(string id, string status) => new()
    {
        ["id"] = id, ["entity"] = "payment", ["order_id"] = "order_1", ["amount"] = 59_000, ["currency"] = "INR", ["status"] = status, ["method"] = "upi", ["amount_refunded"] = 0
    };

    private sealed class ToolError(string message) : Exception(message);

    /// <summary>Plays Razorpay's MCP server: initialize hands out a session id, notifications are accepted with 202, tools/call runs a tool.</summary>
    private sealed class FakeMcpServer : HttpMessageHandler
    {
        public const string SessionId = "sess-123";
        private readonly HashSet<string> _live = new();

        public Dictionary<string, Func<JsonObject, JsonObject>> Tools { get; } = new();
        public List<(string Tool, JsonObject Arguments, string? Session)> Calls { get; } = new();
        public List<string> Authorizations { get; } = new();
        public List<string> Urls { get; } = new();
        public int Initializes { get; private set; }
        public bool UseEventStream { get; set; }
        public bool RefuseAuth { get; set; }

        public void ForgetSessions() => _live.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            if (RefuseAuth)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var method = body["method"]!.GetValue<string>();
            var session = request.Headers.TryGetValues("Mcp-Session-Id", out var s) ? s.First() : null;

            if (method == "initialize")
            {
                Initializes++;
                var id = $"{SessionId}";
                _live.Add(id);
                var reply = Reply(body, new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "razorpay" } });
                reply.Headers.Add("Mcp-Session-Id", id);
                return reply;
            }

            if (session is null || !_live.Contains(session))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (method == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            var name = body["params"]!["name"]!.GetValue<string>();
            var args = body["params"]!["arguments"]!.AsObject();
            Calls.Add((name, args, session));
            if (!Tools.ContainsKey(name))
                return Reply(body, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"tool '{name}' not found: tool not found" }), ["isError"] = true });
            try
            {
                var result = Tools[name](args);
                return Reply(body, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.ToJsonString() }) });
            }
            catch (ToolError e)
            {
                return Reply(body, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = e.Message }), ["isError"] = true });
            }
        }

        private HttpResponseMessage Reply(JsonObject request, JsonObject result)
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString();
            return UseEventStream
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"event: message\ndata: {message}\n\n", Encoding.UTF8, "text/event-stream") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RestStub : HttpMessageHandler
    {
        public string Body { get; set; } = "{}";
        public int Calls { get; private set; }
        public List<(string Method, string Path)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
