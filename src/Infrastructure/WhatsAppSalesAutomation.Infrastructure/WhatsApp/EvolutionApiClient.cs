using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// <see cref="IWhatsAppBridgeClient"/> for a self-hosted Evolution API v2. Every endpoint and field below was exercised against a running
/// v2.3.7 server (docs/whatsapp-provider/PHASE-A-C-ANALYSIS.md, section 9) - nothing here is taken from third-party write-ups.
/// Auth is the <c>apikey</c> header. Registered with <c>RemoveAllLoggers()</c>: the header is not logged by default, but a request that carries
/// a key must never depend on that, and response bodies (which hold QR codes and tokens) must never reach a log.
/// </summary>
public class EvolutionApiClient : IWhatsAppBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Only the events the adapter acts on. Names are the subscribe-form; the bridge delivers them lowercase-dotted.
    private static readonly string[] WebhookEvents = { "CONNECTION_UPDATE", "MESSAGES_UPSERT", "MESSAGES_UPDATE", "SEND_MESSAGE" };

    private readonly HttpClient _http;
    private readonly ILogger<EvolutionApiClient> _logger;

    public EvolutionApiClient(HttpClient http, ILogger<EvolutionApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task PingAsync(BridgeEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        // fetchInstances answers 200 with a valid key and 401 without one, so it proves both reachability and the key.
        using var response = await SendAsync(endpoint, HttpMethod.Get, "/instance/fetchInstances", null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new WhatsAppBridgeException("The bridge rejected the API key.");
        if (!response.IsSuccessStatusCode)
            throw new WhatsAppBridgeException($"The bridge answered {(int)response.StatusCode}.");
    }

    public async Task<BridgeInstance> CreateInstanceAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, "/instance/create",
            new { instanceName, integration = "WHATSAPP-BAILEYS", qrcode = true }, cancellationToken);
        var root = await ReadJsonAsync(response, "create the instance", cancellationToken);

        return new BridgeInstance(
            instanceName,
            Str(root, "hash"),
            Str(Obj(root, "instance"), "status") ?? "connecting",
            ParseQr(Obj(root, "qrcode")));
    }

    public async Task<BridgeQr?> ConnectAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Get, $"/instance/connect/{Uri.EscapeDataString(instanceName)}", null, cancellationToken);
        return ParseQr(await ReadJsonAsync(response, "get a QR code", cancellationToken));
    }

    public async Task<string?> GetStateAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Get, $"/instance/connectionState/{Uri.EscapeDataString(instanceName)}", null, cancellationToken);
        // A deleted instance answers 404 - that is "no such instance", not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var root = await ReadJsonAsync(response, "read the connection state", cancellationToken);
        return Str(Obj(root, "instance"), "state");
    }

    public async Task ConfigureWebhookAsync(
        BridgeEndpoint endpoint, string instanceName, string url, string secretHeaderName, string secret, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, $"/webhook/set/{Uri.EscapeDataString(instanceName)}", new
        {
            webhook = new
            {
                enabled = true,
                url,
                byEvents = false,
                base64 = false,
                headers = new Dictionary<string, string> { [secretHeaderName] = secret },
                events = WebhookEvents,
            }
        }, cancellationToken);
        await ReadJsonAsync(response, "register the webhook", cancellationToken);
    }

    public async Task LogoutAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Delete, $"/instance/logout/{Uri.EscapeDataString(instanceName)}", null, cancellationToken);
        // Not trusted as proof either way (see WhatsAppChannelService) - the caller re-reads the state. A 404 just means nothing to log out.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            _logger.LogWarning("Bridge logout answered {Status}", (int)response.StatusCode);
    }

    public async Task RestartAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(endpoint, HttpMethod.Post, $"/instance/restart/{Uri.EscapeDataString(instanceName)}", null, cancellationToken);
        }
        catch (WhatsAppBridgeException)
        {
            // Observed: the restart answers an error body yet moves the session to "connecting". Best effort by design.
        }
    }

    public async Task DeleteAsync(BridgeEndpoint endpoint, string instanceName, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Delete, $"/instance/delete/{Uri.EscapeDataString(instanceName)}", null, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new WhatsAppBridgeException($"The bridge would not delete the instance ({(int)response.StatusCode}).");
    }

    public async Task<WhatsAppSendResult> SendTextAsync(
        BridgeEndpoint endpoint, string instanceName, string toPhoneNumberE164, string text, CancellationToken cancellationToken = default)
    {
        try
        {
            // Flat { number, text } - the nested textMessage form was rejected by v2.3.7. Digits only: no '+'.
            var number = new string(toPhoneNumberE164.Where(char.IsDigit).ToArray());
            using var response = await SendAsync(endpoint, HttpMethod.Post, $"/message/sendText/{Uri.EscapeDataString(instanceName)}", new { number, text }, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return WhatsAppSendResult.Failed($"The WhatsApp bridge refused the message ({(int)response.StatusCode}).");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var id = Str(Obj(doc.RootElement, "key"), "id");
            return string.IsNullOrEmpty(id)
                ? WhatsAppSendResult.Failed("The WhatsApp bridge accepted the message but returned no message id.")
                : new WhatsAppSendResult(true, id, null, WhatsAppProviders.QrBridge);
        }
        catch (WhatsAppBridgeException ex)
        {
            return WhatsAppSendResult.Failed(ex.Message);
        }
        catch (JsonException)
        {
            return WhatsAppSendResult.Failed("The WhatsApp bridge returned a reply that could not be read.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(BridgeEndpoint endpoint, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{endpoint.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Add("apikey", endpoint.ApiKey);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The message names the host problem, never the request (which carries the key).
            throw new WhatsAppBridgeException(ex is TaskCanceledException ? "The bridge did not answer in time." : "The bridge could not be reached.");
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new WhatsAppBridgeException("The bridge rejected the API key.");
        if (!response.IsSuccessStatusCode)
            throw new WhatsAppBridgeException($"The bridge could not {action} ({(int)response.StatusCode}).");

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new WhatsAppBridgeException($"The bridge could not {action}: unreadable reply.");
        }
    }

    private static BridgeQr? ParseQr(JsonElement element)
    {
        var base64 = Str(element, "base64");
        var code = Str(element, "code");
        return string.IsNullOrEmpty(base64) && string.IsNullOrEmpty(code) ? null : new BridgeQr(base64, code);
    }

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
