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
    private readonly ILogger<MetaWhatsAppCloudApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MetaWhatsAppCloudApiClient(HttpClient httpClient, IOptions<WhatsAppSettings> settings, ILogger<MetaWhatsAppCloudApiClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri($"https://graph.facebook.com/{_settings.ApiVersion}/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
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

            _logger.LogWarning("Meta send failed for {Phone}: {Error}", toPhoneNumberE164, errorMessage);
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
    }
}
