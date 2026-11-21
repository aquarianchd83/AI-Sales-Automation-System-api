using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Infrastructure.Payments;

/// <summary>
/// A minimal client for Razorpay's MCP server (https://mcp.razorpay.com/mcp): JSON-RPC 2.0 over MCP's Streamable HTTP transport. It does the three
/// things the backend needs - <c>initialize</c>, <c>notifications/initialized</c> and <c>tools/call</c> - and reads a reply sent either as plain JSON
/// or as a server-sent event stream.
/// <para>
/// Authentication is the merchant token Razorpay documents for its remote server: <c>Authorization: Basic base64(key_id:key_secret)</c>, set on each
/// request (never on the shared client), so two key pairs can never cross. The session the server opens is remembered per key and endpoint for the
/// life of this instance (one request or job scope), and opened again if the server forgets it.
/// </para>
/// <para>
/// A Razorpay tool answers with one text content block holding Razorpay's own JSON (the same body its REST API returns), or with
/// <c>isError: true</c> and Razorpay's error text. Both come back as a <see cref="RazorpayResult{T}"/>; nothing is thrown for a declined call.
/// </para>
/// </summary>
public class RazorpayMcpClient
{
    public const string DefaultUrl = "https://mcp.razorpay.com/mcp";
    public const string ProtocolVersion = "2025-06-18";
    private const string SessionHeader = "Mcp-Session-Id";
    private const string ProtocolHeader = "MCP-Protocol-Version";

    private readonly HttpClient _http;
    private readonly ILogger<RazorpayMcpClient> _logger;
    private readonly Dictionary<string, string?> _sessions = new();
    private int _nextId;

    public RazorpayMcpClient(HttpClient http, ILogger<RazorpayMcpClient> logger)
    {
        _http = http;
        _logger = logger;
        if (_http.Timeout > TimeSpan.FromSeconds(30))
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Calls one Razorpay MCP tool and returns the JSON Razorpay sent back (the tool's text content, parsed).</summary>
    public async Task<RazorpayResult<JsonElement>> CallToolAsync(
        string endpoint, RazorpayCredentials credentials, string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var session = await EnsureSessionAsync(endpoint, credentials, cancellationToken);
                if (!session.Success)
                    return RazorpayResult<JsonElement>.Fail(session.Error!);

                var args = new JsonObject();
                foreach (var (name, value) in arguments)
                {
                    if (value is not null)
                        args[name] = JsonSerializer.SerializeToNode(value);
                }

                var (status, reply) = await SendAsync(endpoint, credentials, session.Value, "tools/call",
                    new JsonObject { ["name"] = tool, ["arguments"] = args }, expectReply: true, cancellationToken);

                // The server dropped our session (restart, expiry): open a new one and ask again, once.
                if (status is HttpStatusCode.NotFound && session.Value is not null && attempt == 1)
                {
                    _sessions.Remove(SessionKey(endpoint, credentials));
                    continue;
                }

                if (!IsSuccess(status))
                    return RazorpayResult<JsonElement>.Fail(HttpError(status, reply));

                return ReadToolResult(tool, reply);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            _logger.LogWarning(ex, "Razorpay MCP tool {Tool} failed", tool);
            return RazorpayResult<JsonElement>.Fail($"Could not reach Razorpay MCP: {ex.Message}");
        }
    }

    // ── Session ─────────────────────────────────────────────────────────────────────────

    private async Task<RazorpayResult<string?>> EnsureSessionAsync(string endpoint, RazorpayCredentials credentials, CancellationToken cancellationToken)
    {
        var key = SessionKey(endpoint, credentials);
        if (_sessions.TryGetValue(key, out var known))
            return RazorpayResult<string?>.Ok(known);

        var init = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "aisaarthi-backend", ["version"] = "1.0" }
        };

        using var request = Request(endpoint, credentials, null, "initialize", init, withId: true, sendProtocol: false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var reply = await ReadReplyAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return RazorpayResult<string?>.Fail(HttpError(response.StatusCode, reply));
        if (reply?["error"] is JsonNode error)
            return RazorpayResult<string?>.Fail("Razorpay MCP refused to start a session: " + (error["message"]?.GetValue<string>() ?? "unknown error"));

        // A stateless server sends no session id; every later request then simply goes without one.
        var session = response.Headers.TryGetValues(SessionHeader, out var values) ? values.FirstOrDefault() : null;

        var (status, _) = await SendAsync(endpoint, credentials, session, "notifications/initialized", null, expectReply: false, cancellationToken);
        if (!IsSuccess(status))
            return RazorpayResult<string?>.Fail($"Razorpay MCP did not accept the session ({(int)status}).");

        _sessions[key] = session;
        return RazorpayResult<string?>.Ok(session);
    }

    private static string SessionKey(string endpoint, RazorpayCredentials credentials) => endpoint + "|" + credentials.KeyId;

    // ── Wire ────────────────────────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, JsonNode? Reply)> SendAsync(
        string endpoint, RazorpayCredentials credentials, string? session, string method, JsonObject? parameters, bool expectReply, CancellationToken cancellationToken)
    {
        using var request = Request(endpoint, credentials, session, method, parameters, withId: expectReply, sendProtocol: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var reply = expectReply ? await ReadReplyAsync(response, cancellationToken) : null;
        return (response.StatusCode, reply);
    }

    private HttpRequestMessage Request(
        string endpoint, RazorpayCredentials credentials, string? session, string method, JsonObject? parameters, bool withId, bool sendProtocol)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (withId)
            message["id"] = Interlocked.Increment(ref _nextId);
        if (parameters is not null)
            message["params"] = parameters;

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.KeyId}:{credentials.KeySecret}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session is not null)
            request.Headers.TryAddWithoutValidation(SessionHeader, session);
        if (sendProtocol)
            request.Headers.TryAddWithoutValidation(ProtocolHeader, ProtocolVersion);
        return request;
    }

    /// <summary>The JSON-RPC response in the body: the body itself for application/json, or the first event carrying a response for an SSE stream.</summary>
    private static async Task<JsonNode?> ReadReplyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return JsonNode.Parse(text);

        // SSE: events separated by a blank line; each "data:" line is part of the event's payload. Notifications (no id) are skipped.
        foreach (var block in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", block.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart()));
            if (data.Length == 0)
                continue;
            var node = JsonNode.Parse(data);
            if (node?["id"] is not null && (node["result"] is not null || node["error"] is not null))
                return node;
        }
        return null;
    }

    private RazorpayResult<JsonElement> ReadToolResult(string tool, JsonNode? reply)
    {
        if (reply is null)
            return RazorpayResult<JsonElement>.Fail("Razorpay MCP sent no reply.");

        if (reply["error"] is JsonNode rpcError)
        {
            var message = rpcError["message"]?.GetValue<string>() ?? "unknown error";
            _logger.LogWarning("Razorpay MCP tool {Tool} refused: {Error}", tool, message);
            return RazorpayResult<JsonElement>.Fail(message);
        }

        var result = reply["result"];
        var text = string.Concat((result?["content"] as JsonArray ?? new JsonArray())
            .Where(c => c?["type"]?.GetValue<string>() == "text")
            .Select(c => c!["text"]?.GetValue<string>() ?? string.Empty));

        if (result?["isError"]?.GetValue<bool>() == true)
        {
            var error = ErrorText(text);
            _logger.LogWarning("Razorpay MCP tool {Tool} failed: {Error}", tool, error);
            return RazorpayResult<JsonElement>.Fail(error);
        }

        // Newer servers may also send the same object as structuredContent; either will do.
        if (result?["structuredContent"] is JsonObject structured)
            return RazorpayResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(structured));
        if (text.Length == 0)
            return RazorpayResult<JsonElement>.Fail("Razorpay MCP returned an empty result.");

        using var doc = JsonDocument.Parse(text);
        return RazorpayResult<JsonElement>.Ok(doc.RootElement.Clone());
    }

    /// <summary>Razorpay's own description where the text carries its error JSON, else the text as it is.</summary>
    private static string ErrorText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Razorpay MCP reported an error.";
        var start = text.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..]);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } description)
                    return description;
            }
            catch (JsonException)
            {
            }
        }
        return text.Length <= 300 ? text : text[..300];
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static string HttpError(HttpStatusCode status, JsonNode? reply) =>
        reply?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message
            ? message
            : status == HttpStatusCode.Unauthorized ? "Razorpay MCP did not accept the API keys." : $"Razorpay MCP returned {(int)status}.";
}
