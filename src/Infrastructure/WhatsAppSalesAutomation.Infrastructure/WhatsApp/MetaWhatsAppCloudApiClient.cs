using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Real Meta WhatsApp Cloud API client, selected via <c>WhatsApp:Provider = "Meta"</c>. Never
/// exercised against a live account in this codebase - no WhatsApp Business Account was available
/// when Phase 3 was built (see the Phase 1 open assumptions) - so treat first use against production
/// credentials as the actual first test of this class, not as already-verified code.
/// </summary>
public class MetaWhatsAppCloudApiClient : IWhatsAppService
{
    private readonly HttpClient _httpClient;
    private readonly WhatsAppSettings _settings;
    private readonly IWhatsAppTokenStore _tokenStore;
    private readonly ILogger<MetaWhatsAppCloudApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MetaWhatsAppCloudApiClient(
        HttpClient httpClient, IOptions<WhatsAppSettings> settings, IWhatsAppTokenStore tokenStore, ILogger<MetaWhatsAppCloudApiClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _tokenStore = tokenStore;
        _logger = logger;

        var baseUrl = _settings.ApiBaseUrl.EndsWith('/') ? _settings.ApiBaseUrl : $"{_settings.ApiBaseUrl}/";
        _httpClient.BaseAddress = new Uri($"{baseUrl}{_settings.ApiVersion}/");
        // No Authorization header set here (unlike before WhatsAppTokenRefreshService existed) - the
        // bearer token is fetched fresh from IWhatsAppTokenStore immediately before every call via
        // ApplyCurrentTokenAsync, since WhatsAppTokenRefreshJob can replace it at any time while this
        // client instance (one per DI scope) is alive.
        // Without an explicit timeout, a stuck DNS lookup or dead connection can hang far past
        // any reasonable wait (observed: 5+ minutes with no error) instead of failing fast into
        // the existing app-level retry/backoff (Messaging:MaxRetryAttempts / RetryBackoffMinutes).
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    private async Task ApplyCurrentTokenAsync(CancellationToken cancellationToken)
    {
        var current = await _tokenStore.GetCurrentAsync(cancellationToken);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
    }

    public async Task<WhatsAppSendResult> SendTemplateMessageAsync(
        string toPhoneNumberE164,
        string templateName,
        string languageCode,
        IReadOnlyList<string> parameterValues,
        string? mediaUrl = null,
        CancellationToken cancellationToken = default)
    {
        var components = new List<object>();

        if (!string.IsNullOrWhiteSpace(mediaUrl))
        {
            // Anonymous types cannot have a computed member name, so the "image"/"video" branch
            // is spelled out explicitly rather than building the property name from mediaKind.
            object mediaParameter = InferMediaKind(mediaUrl) == "video"
                ? new { type = "video", video = new { link = mediaUrl } }
                : new { type = "image", image = new { link = mediaUrl } };

            components.Add(new { type = "header", parameters = new[] { mediaParameter } });
        }

        if (parameterValues.Count > 0)
        {
            components.Add(new
            {
                type = "body",
                parameters = parameterValues.Select(v => new { type = "text", text = v }).ToArray()
            });
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            to = toPhoneNumberE164.TrimStart('+'), // Meta expects the number without the leading '+'
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = languageCode },
                components
            }
        };

        return await PostMessageAsync(payload, toPhoneNumberE164, cancellationToken);
    }

    public async Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to = toPhoneNumberE164.TrimStart('+'),
            type = "text",
            text = new { body = text }
        };

        return await PostMessageAsync(payload, toPhoneNumberE164, cancellationToken);
    }

    private async Task<WhatsAppSendResult> PostMessageAsync(object payload, string toPhoneNumberE164, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyCurrentTokenAsync(cancellationToken);
            using var response = await _httpClient.PostAsJsonAsync($"{_settings.PhoneNumberId}/messages", payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var success = JsonSerializer.Deserialize<MetaSendResponse>(body, JsonOptions);
                var messageId = success?.Messages?.FirstOrDefault()?.Id;

                if (string.IsNullOrEmpty(messageId))
                    return WhatsAppSendResult.Failed("Meta returned success but no message id.");

                return WhatsAppSendResult.Succeeded(messageId);
            }

            var error = JsonSerializer.Deserialize<MetaErrorResponse>(body, JsonOptions);
            var errorMessage = error?.Error?.Message ?? $"Meta API returned {(int)response.StatusCode}.";

            // The top-level message (e.g. "(#131005) Access denied") is often too generic to act on;
            // subcode/details/fbtrace_id are what Meta support actually needs to diagnose it, and what
            // usually names the real cause (e.g. recipient not in the test allow-list vs. a real
            // permission problem) - log the full raw body rather than re-guessing from the summary.
            _logger.LogWarning(
                "Meta send failed for {Phone}: {Error} (code={Code}, subcode={Subcode}, details={Details}, fbtrace_id={FbtraceId}). Raw: {Body}",
                toPhoneNumberE164, errorMessage, error?.Error?.Code, error?.Error?.ErrorSubcode,
                error?.Error?.ErrorData?.Details, error?.Error?.FbtraceId, body);
            return WhatsAppSendResult.Failed(errorMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Meta send threw for {Phone}", toPhoneNumberE164);
            return WhatsAppSendResult.Failed($"Request to WhatsApp API failed: {ex.Message}");
        }
    }

    public async Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        form.Add(streamContent, "file", "upload");
        form.Add(new StringContent("whatsapp"), "messaging_product");

        await ApplyCurrentTokenAsync(cancellationToken);
        using var response = await _httpClient.PostAsync($"{_settings.PhoneNumberId}/media", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = JsonSerializer.Deserialize<MetaErrorResponse>(body, JsonOptions);
            throw new InvalidOperationException($"Meta media upload failed: {error?.Error?.Message ?? body}");
        }

        var result = JsonSerializer.Deserialize<MetaMediaUploadResponse>(body, JsonOptions);
        return result?.Id ?? throw new InvalidOperationException("Meta returned success but no media id.");
    }

    /// <summary>Meta's template header requires the media kind up front; there is no generic "file"
    /// header type, so this infers image vs. video from the extension. Good enough for the two
    /// content types the Media Library actually accepts (see MediaOptions.AllowedContentTypes) -
    /// revisit if that allow-list grows to audio/documents.</summary>
    private static string InferMediaKind(string mediaUrl)
    {
        var extension = Path.GetExtension(new Uri(mediaUrl, UriKind.RelativeOrAbsolute).ToString()).ToLowerInvariant();
        return extension is ".mp4" or ".3gp" ? "video" : "image";
    }

    private class MetaSendResponse
    {
        [JsonPropertyName("messages")]
        public List<MetaMessageId>? Messages { get; set; }
    }

    private class MetaMessageId
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private class MetaMediaUploadResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private class MetaErrorResponse
    {
        [JsonPropertyName("error")]
        public MetaError? Error { get; set; }
    }

    private class MetaError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("code")]
        public int? Code { get; set; }

        [JsonPropertyName("error_subcode")]
        public int? ErrorSubcode { get; set; }

        [JsonPropertyName("error_data")]
        public MetaErrorData? ErrorData { get; set; }

        [JsonPropertyName("fbtrace_id")]
        public string? FbtraceId { get; set; }
    }

    /// <summary>Meta's top-level error message (e.g. "(#131005) Access denied") is often generic;
    /// "details" here is what actually names the cause (e.g. recipient not in the allowed test list).</summary>
    private class MetaErrorData
    {
        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }
}
