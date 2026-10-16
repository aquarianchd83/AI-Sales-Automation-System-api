using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Sms;
using WhatsAppSalesAutomation.Infrastructure.Verification;

namespace WhatsAppSalesAutomation.Infrastructure.Integrations;

/// <summary>How one outside service is handled, bound from "Integrations:ConsentSms" / "Integrations:DndCheck", kept by the Platform
/// Admin Console's Integrations page (the key encrypted). Absent means the simulator.</summary>
public class IntegrationChannelOptions
{
    public string Mode { get; set; } = PlatformIntegrationSettingsService.SimulatorMode;

    public string ApiUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ApiKeyHeader { get; set; } = PlatformIntegrationSettingsService.DefaultApiKeyHeader;

    public string SenderId { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = PlatformIntegrationSettingsService.DefaultTimeoutSeconds;

    public bool UsesApi => string.Equals(Mode, PlatformIntegrationSettingsService.ApiMode, StringComparison.OrdinalIgnoreCase);

    public IntegrationApiSettings ToSettings() =>
        new(ApiUrl, ApiKey, string.IsNullOrWhiteSpace(ApiKeyHeader) ? PlatformIntegrationSettingsService.DefaultApiKeyHeader : ApiKeyHeader,
            SenderId, TimeoutSeconds > 0 ? TimeoutSeconds : PlatformIntegrationSettingsService.DefaultTimeoutSeconds);
}

public class ConsentSmsIntegrationOptions : IntegrationChannelOptions
{
}

public class DndCheckIntegrationOptions : IntegrationChannelOptions
{
}

/// <summary>The request every third-party call goes through: a JSON POST with the key in a header ("Authorization" sends it as a
/// Bearer token, any other header name sends it as is) and a timeout.</summary>
internal static class ThirdPartyHttp
{
    public static async Task<(HttpStatusCode Status, string Body)> PostJsonAsync(
        HttpClient http, IntegrationApiSettings settings, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        var value = string.Equals(settings.ApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase) ? $"Bearer {settings.ApiKey}" : settings.ApiKey;
        request.Headers.TryAddWithoutValidation(settings.ApiKeyHeader, value);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var response = await http.SendAsync(request, timeout.Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
    }

    /// <summary>A 5xx, 429 or 408 is worth retrying; any other rejection is permanent.</summary>
    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout;

    public static JsonElement? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Read(JsonElement? root, params string[] names)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj)
            return null;
        foreach (var name in names)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return property.Value.ToString();
            }
        }
        return null;
    }
}

/// <summary>
/// Sends the consent SMS through a third-party API. The wire contract is deliberately generic until a provider is chosen; a provider
/// whose field names differ is matched here, in this one class, without touching the workflow.
/// Request: POST { to, message, reference, senderId }. Response: 2xx with { messageId } (or id); any other status is a rejection with
/// { message } (or error) as the reason. Delivery is reported later by a receipt (SmsReceiptsController), not in this call.
/// </summary>
public static class ThirdPartyConsentSmsClient
{
    public static async Task<ConsentSmsSendResult> SendAsync(
        HttpClient http, IntegrationApiSettings settings, ConsentSmsRequest sms, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            return new ConsentSmsSendResult(false, null, "The consent SMS API address or key is not set up in the platform's Integrations settings.");

        try
        {
            var (status, body) = await ThirdPartyHttp.PostJsonAsync(http, settings,
                new { to = sms.ToPhoneE164, message = sms.Body, reference = sms.IdempotencyKey, senderId = settings.SenderId }, cancellationToken);
            var json = ThirdPartyHttp.Parse(body);

            if ((int)status is >= 200 and < 300)
                return new ConsentSmsSendResult(true, ThirdPartyHttp.Read(json, "messageId", "id", "message_id"), null);

            var reason = ThirdPartyHttp.Read(json, "message", "error", "reason") ?? $"The SMS provider returned {(int)status}.";
            logger.LogWarning("Consent SMS provider rejected a request ({Status}): {Reason}", (int)status, reason);
            return new ConsentSmsSendResult(false, null, reason, Transient: ThirdPartyHttp.IsTransient(status));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Consent SMS request failed");
            return new ConsentSmsSendResult(false, null, "The SMS provider could not be reached.", Transient: true);
        }
    }
}

/// <summary>
/// Checks numbers against a third-party DND API. Generic wire contract, matched to a provider in this one class.
/// Request: POST { requestId, numbers: [{ reference, phone }] }. Response: 2xx with { requestId, results: [{ reference, status,
/// reasonCode, reason }] }, status being pass, fail or pending. Anything missing from the answer, or a failed call, is an Error for
/// that number - never a silent pass.
/// </summary>
public static class ThirdPartyDndClient
{
    public static async Task<PhoneVerificationResponse> VerifyAsync(
        HttpClient http, IntegrationApiSettings settings, IReadOnlyList<PhoneVerificationItem> items, ILogger logger, CancellationToken cancellationToken = default)
    {
        var requestId = $"dnd-{Guid.NewGuid():N}";

        PhoneVerificationResponse AllError(string reason) => new(requestId,
            items.Select(i => new PhoneVerificationItemResult(i.ReferenceId, LeadDiscoveryVerificationStatus.Error, "PROVIDER_ERROR", reason)).ToList());

        if (string.IsNullOrWhiteSpace(settings.ApiUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            return AllError("The DND check API address or key is not set up in the platform's Integrations settings.");

        try
        {
            var (status, body) = await ThirdPartyHttp.PostJsonAsync(http, settings,
                new { requestId, numbers = items.Select(i => new { reference = i.ReferenceId, phone = i.PhoneE164 }) }, cancellationToken);
            var json = ThirdPartyHttp.Parse(body);

            if ((int)status is < 200 or >= 300)
            {
                var reason = ThirdPartyHttp.Read(json, "message", "error", "reason") ?? $"The DND provider returned {(int)status}.";
                logger.LogWarning("DND provider rejected a request ({Status}): {Reason}", (int)status, reason);
                return AllError(reason);
            }

            var answers = new Dictionary<Guid, PhoneVerificationItemResult>();
            if (json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in results.EnumerateArray())
                {
                    if (!Guid.TryParse(ThirdPartyHttp.Read(result, "reference"), out var reference))
                        continue;
                    answers[reference] = new PhoneVerificationItemResult(
                        reference,
                        ParseStatus(ThirdPartyHttp.Read(result, "status")),
                        ThirdPartyHttp.Read(result, "reasonCode", "reason_code"),
                        ThirdPartyHttp.Read(result, "reason", "message"));
                }
            }

            return new PhoneVerificationResponse(
                ThirdPartyHttp.Read(json, "requestId", "request_id") ?? requestId,
                items.Select(i => answers.TryGetValue(i.ReferenceId, out var answer)
                    ? answer
                    : new PhoneVerificationItemResult(i.ReferenceId, LeadDiscoveryVerificationStatus.Error, "NO_ANSWER", "The DND provider gave no answer for this number.")).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "DND check request failed");
            return AllError("The DND provider could not be reached.");
        }
    }

    internal static LeadDiscoveryVerificationStatus ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" or "clear" => LeadDiscoveryVerificationStatus.Pass,
        "fail" or "failed" or "dnd" or "blocked" => LeadDiscoveryVerificationStatus.Fail,
        "pending" => LeadDiscoveryVerificationStatus.Pending,
        _ => LeadDiscoveryVerificationStatus.Error,
    };
}

/// <summary>Sends the consent SMS the way the platform admin chose: through the third-party API, or by the simulator (the default).
/// Read per request, so a save on the Integrations page is live without a restart.</summary>
public class RoutingConsentSmsSender : IConsentSmsSender
{
    private readonly IOptionsSnapshot<ConsentSmsIntegrationOptions> _options;
    private readonly SimulatedConsentSmsSender _simulator;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RoutingConsentSmsSender> _logger;

    public RoutingConsentSmsSender(
        IOptionsSnapshot<ConsentSmsIntegrationOptions> options, SimulatedConsentSmsSender simulator, IHttpClientFactory http, ILogger<RoutingConsentSmsSender> logger)
    {
        _options = options;
        _simulator = simulator;
        _http = http;
        _logger = logger;
    }

    public Task<ConsentSmsSendResult> SendAsync(ConsentSmsRequest request, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        return options.UsesApi
            ? ThirdPartyConsentSmsClient.SendAsync(_http.CreateClient(), options.ToSettings(), request, _logger, cancellationToken)
            : _simulator.SendAsync(request, cancellationToken);
    }
}

/// <summary>Checks DND the way the platform admin chose: through the third-party API, or by the simulator (the default).</summary>
public class RoutingPhoneVerificationProvider : IPhoneVerificationProvider
{
    private readonly IOptionsSnapshot<DndCheckIntegrationOptions> _options;
    private readonly SimulatedPhoneVerificationProvider _simulator;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RoutingPhoneVerificationProvider> _logger;

    public RoutingPhoneVerificationProvider(
        IOptionsSnapshot<DndCheckIntegrationOptions> options, SimulatedPhoneVerificationProvider simulator, IHttpClientFactory http, ILogger<RoutingPhoneVerificationProvider> logger)
    {
        _options = options;
        _simulator = simulator;
        _http = http;
        _logger = logger;
    }

    public Task<PhoneVerificationResponse> VerifyAsync(
        Guid tenantId, IReadOnlyList<PhoneVerificationItem> items, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        return options.UsesApi
            ? ThirdPartyDndClient.VerifyAsync(_http.CreateClient(), options.ToSettings(), items, _logger, cancellationToken)
            : _simulator.VerifyAsync(tenantId, items, cancellationToken);
    }
}

/// <summary>Makes one real call with the settings on screen, on the same code a real send uses.</summary>
public class IntegrationTester : IIntegrationTester
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<IntegrationTester> _logger;

    public IntegrationTester(IHttpClientFactory http, ILogger<IntegrationTester> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<DeliveryTestResultDto> SendConsentSmsAsync(IntegrationApiSettings settings, string toPhoneE164, CancellationToken cancellationToken = default)
    {
        var result = await ThirdPartyConsentSmsClient.SendAsync(_http.CreateClient(), settings,
            new ConsentSmsRequest(toPhoneE164, "Test message from your sales platform. If you can read it, the SMS integration works.", $"test-{Guid.NewGuid():N}"),
            _logger, cancellationToken);

        return result.Accepted
            ? new DeliveryTestResultDto(true, $"The SMS provider accepted the test message to {toPhoneE164}" + (result.ProviderMessageId is null ? "." : $" (reference {result.ProviderMessageId})."))
            : new DeliveryTestResultDto(false, $"The SMS provider did not accept the message: {result.FailureReason}");
    }

    public async Task<DeliveryTestResultDto> CheckDndAsync(IntegrationApiSettings settings, string phoneE164, CancellationToken cancellationToken = default)
    {
        var response = await ThirdPartyDndClient.VerifyAsync(_http.CreateClient(), settings,
            new[] { new PhoneVerificationItem(Guid.NewGuid(), phoneE164) }, _logger, cancellationToken);
        var result = response.Results[0];

        return result.Status switch
        {
            LeadDiscoveryVerificationStatus.Pass => new DeliveryTestResultDto(true, $"The DND provider answered: {phoneE164} passes (not restricted)."),
            LeadDiscoveryVerificationStatus.Fail => new DeliveryTestResultDto(true, $"The DND provider answered: {phoneE164} is restricted" + (result.Reason is null ? "." : $" ({result.Reason}).")),
            LeadDiscoveryVerificationStatus.Pending => new DeliveryTestResultDto(true, "The DND provider accepted the request but has not answered yet."),
            _ => new DeliveryTestResultDto(false, $"The DND check failed: {result.Reason}"),
        };
    }
}
