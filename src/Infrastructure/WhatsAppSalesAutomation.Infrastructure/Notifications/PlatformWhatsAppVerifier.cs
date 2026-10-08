using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

/// <summary>
/// Verifies the platform's WhatsApp number the cheapest way Meta allows: <c>GET /{version}/{phone-number-id}</c> with the saved token (a 200 proves the
/// token is valid and reaches that exact number), then, when a WhatsApp Business Account id is saved, <c>GET /{version}/{waba-id}</c> to prove the
/// token can see the account the templates live in. Nothing is sent to anyone. The token travels in the Authorization header, never the URL, and the
/// typed client's loggers are removed (see DependencyInjection) - it carries the token.
/// </summary>
public class PlatformWhatsAppVerifier : IPlatformWhatsAppVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IAppSettingsStore _store;

    public PlatformWhatsAppVerifier(HttpClient httpClient, IAppSettingsStore store)
    {
        _httpClient = httpClient;
        _store = store;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<DeliveryTestResultDto> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetAllAsync(cancellationToken);
        var phoneId = Get(stored, PlatformWhatsAppSettingsService.PhoneNumberIdKey);
        var waba = Get(stored, PlatformWhatsAppSettingsService.BusinessAccountIdKey);
        var token = Get(stored, PlatformWhatsAppSettingsService.AccessTokenKey);
        var version = Or(Get(stored, PlatformWhatsAppSettingsService.ApiVersionKey), PlatformWhatsAppSettingsService.DefaultApiVersion);
        var baseUrl = Or(Get(stored, PlatformWhatsAppSettingsService.ApiBaseUrlKey), PlatformWhatsAppSettingsService.DefaultApiBaseUrl);

        if (phoneId.Length == 0 || token.Length == 0)
            return new DeliveryTestResultDto(false, "Save the phone number id and access token first, then verify the connection.");

        var root = new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), version + "/");

        var phone = await AskAsync(root, $"{Uri.EscapeDataString(phoneId)}?fields=display_phone_number,verified_name", token, cancellationToken);
        if (phone.Error is not null)
            return new DeliveryTestResultDto(false, $"Meta rejected the phone number id or access token: {phone.Error}");

        MetaPhoneNumber? number = null;
        try
        {
            number = JsonSerializer.Deserialize<MetaPhoneNumber>(phone.Body!, JsonOptions);
        }
        catch (JsonException)
        {
            // The call succeeded; only the display details are missing.
        }

        var summary = string.IsNullOrWhiteSpace(number?.DisplayPhoneNumber)
            ? "The access token works for the phone number id."
            : $"Connected to {number.DisplayPhoneNumber}{(string.IsNullOrWhiteSpace(number.VerifiedName) ? string.Empty : $" ({number.VerifiedName})")}.";

        if (waba.Length == 0)
            return new DeliveryTestResultDto(true, summary + " No WhatsApp Business Account id is saved, so notice templates cannot be managed.");

        var account = await AskAsync(root, $"{Uri.EscapeDataString(waba)}?fields=name", token, cancellationToken);
        return account.Error is null
            ? new DeliveryTestResultDto(true, summary + " The WhatsApp Business Account is reachable too.")
            : new DeliveryTestResultDto(false, summary + $" But the WhatsApp Business Account id was rejected: {account.Error}");
    }

    private async Task<(string? Body, string? Error)> AskAsync(Uri root, string relative, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode ? (body, null) : (null, $"HTTP {(int)response.StatusCode} - {DescribeMetaError(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, $"could not reach Meta ({ex.GetType().Name}).");
        }
    }

    private static string DescribeMetaError(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<MetaErrorEnvelope>(body, JsonOptions)?.Error;
            if (!string.IsNullOrWhiteSpace(error?.Message))
                return error.Code is null ? error.Message : $"{error.Message} (code {error.Code})";
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return "no error message in the response.";
    }

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private sealed class MetaPhoneNumber
    {
        [JsonPropertyName("display_phone_number")]
        public string? DisplayPhoneNumber { get; set; }

        [JsonPropertyName("verified_name")]
        public string? VerifiedName { get; set; }
    }

    private sealed class MetaErrorEnvelope
    {
        public MetaError? Error { get; set; }
    }

    private sealed class MetaError
    {
        public string? Message { get; set; }

        public int? Code { get; set; }
    }
}
