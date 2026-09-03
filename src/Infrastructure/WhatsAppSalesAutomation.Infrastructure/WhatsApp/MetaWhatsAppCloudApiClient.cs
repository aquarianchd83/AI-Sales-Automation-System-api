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

    public async Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.WhatsAppBusinessAccountId))
        {
            _logger.LogWarning("WhatsApp template sync skipped: WhatsAppBusinessAccountId is not configured.");
            return Array.Empty<WhatsAppRemoteTemplate>();
        }

        var results = new List<WhatsAppRemoteTemplate>();

        await ApplyCurrentTokenAsync(cancellationToken);

        // Meta paginates at 100/page by default for this endpoint; following paging.next until it's
        // absent is the documented way to get the full list rather than assuming one page is everything -
        // a WABA with more templates than that would otherwise silently look fully synced when it isn't.
        string? nextUrl = $"{_settings.WhatsAppBusinessAccountId}/message_templates?fields=id,name,language,status,category&limit=100";

        while (nextUrl is not null)
        {
            // nextUrl becomes an absolute Meta URL from the second page onward (paging.next is a full
            // URL, not a relative path) - GetAsync accepts either against a client with BaseAddress set.
            using var response = await _httpClient.GetAsync(nextUrl, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = JsonSerializer.Deserialize<MetaErrorResponse>(body, JsonOptions);
                _logger.LogWarning(
                    "Meta template list fetch failed: {Error} (code={Code}, fbtrace_id={FbtraceId}). Raw: {Body}",
                    error?.Error?.Message, error?.Error?.Code, error?.Error?.FbtraceId, body);
                break;
            }

            var parsed = JsonSerializer.Deserialize<MetaTemplateListResponse>(body, JsonOptions);
            if (parsed?.Data is not null)
            {
                results.AddRange(parsed.Data
                    .Where(t => t.Id is not null && t.Name is not null && t.Language is not null && t.Status is not null && t.Category is not null)
                    .Select(t => new WhatsAppRemoteTemplate(t.Id!, t.Name!, t.Language!, t.Status!, t.Category!)));
            }

            nextUrl = parsed?.Paging?.Next;
        }

        return results;
    }

    public async Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.WhatsAppBusinessAccountId))
            return new WhatsAppTemplateSubmitResult(false, null, null, "WhatsAppBusinessAccountId is not configured.");

        var payload = new
        {
            name = submission.Name,
            language = submission.Language,
            category = submission.Category.ToUpperInvariant(),
            components = BuildTemplateComponents(submission)
        };

        try
        {
            await ApplyCurrentTokenAsync(cancellationToken);
            using var response = await _httpClient.PostAsJsonAsync($"{_settings.WhatsAppBusinessAccountId}/message_templates", payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return FailedTemplateSubmit(submission.Name, "create", body);

            var created = JsonSerializer.Deserialize<MetaTemplateSubmitResponse>(body, JsonOptions);
            if (string.IsNullOrEmpty(created?.Id))
                return new WhatsAppTemplateSubmitResult(false, null, null, "Meta returned success but no template id.");

            return new WhatsAppTemplateSubmitResult(true, created.Id, created.Status, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Meta template create threw for {Name}", submission.Name);
            return new WhatsAppTemplateSubmitResult(false, null, null, $"Request to WhatsApp API failed: {ex.Message}");
        }
    }

    public async Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        var payload = new { components = BuildTemplateComponents(submission) };

        try
        {
            await ApplyCurrentTokenAsync(cancellationToken);
            using var response = await _httpClient.PostAsJsonAsync(metaTemplateId, payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return FailedTemplateSubmit(submission.Name, "update", body);

            // The edit endpoint's own response does not reliably include a fresh status the way create
            // does - MessageTemplateSyncJob's existing pull half (GetMessageTemplatesAsync) is what
            // picks up the real post-edit status on its next run, so null here is honest, not a gap.
            return new WhatsAppTemplateSubmitResult(true, metaTemplateId, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Meta template update threw for {MetaTemplateId}", metaTemplateId);
            return new WhatsAppTemplateSubmitResult(false, metaTemplateId, null, $"Request to WhatsApp API failed: {ex.Message}");
        }
    }

    private WhatsAppTemplateSubmitResult FailedTemplateSubmit(string name, string action, string rawBody)
    {
        var error = JsonSerializer.Deserialize<MetaErrorResponse>(rawBody, JsonOptions);
        var errorMessage = error?.Error?.Message ?? "Meta API returned an error.";

        _logger.LogWarning(
            "Meta template {Action} failed for {Name}: {Error} (code={Code}, subcode={Subcode}, details={Details}, fbtrace_id={FbtraceId}). Raw: {Body}",
            action, name, errorMessage, error?.Error?.Code, error?.Error?.ErrorSubcode,
            error?.Error?.ErrorData?.Details, error?.Error?.FbtraceId, rawBody);

        return new WhatsAppTemplateSubmitResult(false, null, null, errorMessage);
    }

    /// <summary>Meta requires an "example" for every numbered placeholder in a BODY component - see
    /// TemplatePlaceholderResolver.ToMetaTemplateBody's own doc comment for where ExampleValues comes
    /// from. Built as a plain Dictionary rather than an anonymous type since the "example" key is
    /// only added conditionally (a template with no placeholders has nothing to give an example for,
    /// and Meta rejects an empty body_text example array as readily as a missing one).</summary>
    private static object[] BuildTemplateComponents(WhatsAppTemplateSubmission submission)
    {
        var component = new Dictionary<string, object?>
        {
            ["type"] = "BODY",
            ["text"] = submission.MetaBodyText
        };

        if (submission.ExampleValues.Count > 0)
            component["example"] = new { body_text = new[] { submission.ExampleValues.ToArray() } };

        return new object[] { component };
    }

    private class MetaTemplateSubmitResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
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

    private class MetaTemplateListResponse
    {
        [JsonPropertyName("data")]
        public List<MetaTemplateItem>? Data { get; set; }

        [JsonPropertyName("paging")]
        public MetaPaging? Paging { get; set; }
    }

    private class MetaTemplateItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }

    private class MetaPaging
    {
        [JsonPropertyName("next")]
        public string? Next { get; set; }
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
