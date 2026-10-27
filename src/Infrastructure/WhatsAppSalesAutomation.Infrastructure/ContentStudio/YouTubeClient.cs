using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Publishing;

namespace WhatsAppSalesAutomation.Infrastructure.ContentStudio;

/// <summary>
/// YouTube and Google sign-in. The platform's client secret and every token travel in form bodies or headers - never in an address - and
/// nothing here logs one. Every failure becomes a <see cref="SocialProviderException"/> whose kind says what to do about it: a busy service
/// before anything was sent is temporary; quota, policy and rejected files are permanent; a refused sign-in means "connect again"; and a break
/// in the middle of sending the video is ambiguous, because the video may already be there. The upload address Google hands back is only
/// followed if it is on Google's own host.
/// </summary>
public sealed class YouTubeClient : IYouTubeClient
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string ChannelEndpoint = "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true";
    private const string StatisticsEndpoint = "https://www.googleapis.com/youtube/v3/videos?part=statistics&maxResults=50&id=";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
    public const string Scopes = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<SocialOptions> _options;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<YouTubeClient> _logger;

    public YouTubeClient(HttpClient http, IOptionsSnapshot<SocialOptions> options, IDateTimeProvider clock, ILogger<YouTubeClient> logger)
    {
        _http = http;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public bool IsConfigured => _options.Value.YouTubeConfigured;

    public string BuildLoginUrl(string redirectUri, string state) =>
        AuthEndpoint + "?" + string.Join("&", new[]
        {
            "client_id=" + Uri.EscapeDataString(_options.Value.GoogleClientId.Trim()),
            "redirect_uri=" + Uri.EscapeDataString(redirectUri),
            "response_type=code",
            "scope=" + Uri.EscapeDataString(Scopes),
            "access_type=offline",
            "prompt=consent", // Google only hands out the long-term token when it is asked to show the consent screen
            "include_granted_scopes=false",
            "state=" + Uri.EscapeDataString(state),
        });

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default) =>
        TokenRequestAsync(new Dictionary<string, string> { ["code"] = code, ["redirect_uri"] = redirectUri, ["grant_type"] = "authorization_code" }, ct);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        TokenRequestAsync(new Dictionary<string, string> { ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }, ct);

    private async Task<OAuthTokens> TokenRequestAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = _options.Value.GoogleClientId.Trim();
        fields["client_secret"] = _options.Value.GoogleClientSecret.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        using var response = await SendAsync(request, SocialFailure.Transient, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ErrorOf(response, ct);
            if (error.Reason is "invalid_grant" or "invalid_client" or "unauthorized_client")
                throw new SocialProviderException(
                    fields["grant_type"] == "refresh_token"
                        ? "Google no longer accepts the saved sign-in. Connect your YouTube account again."
                        : "Google would not accept that sign-in. Please click Connect again.",
                    fields["grant_type"] == "refresh_token" ? SocialFailure.NeedsReconnect : SocialFailure.Permanent);
            throw Fail(response.StatusCode, error, SocialFailure.Transient);
        }

        using var doc = await ParseAsync(response, ct);
        var root = doc.RootElement;
        var access = Text(root, "access_token") ?? throw new SocialProviderException("Google did not give an access token.", SocialFailure.Permanent);
        var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3300;
        return new OAuthTokens(access, Text(root, "refresh_token"), _clock.UtcNow.AddSeconds(expires), Text(root, "scope") ?? string.Empty);
    }

    public async Task<YouTubeChannel?> GetChannelAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ChannelEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, SocialFailure.Transient, ct);
        if (!response.IsSuccessStatusCode)
            throw Fail(response.StatusCode, await ErrorOf(response, ct), SocialFailure.Transient);

        using var doc = await ParseAsync(response, ct);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return null;
        var channel = items[0];
        var id = Text(channel, "id");
        var title = channel.TryGetProperty("snippet", out var snippet) ? Text(snippet, "title") : null;
        return id is null ? null : new YouTubeChannel(id, title ?? "YouTube channel");
    }

    public async Task<string> UploadAsync(string accessToken, YouTubeUploadRequest upload, Stream video, long length, CancellationToken ct = default)
    {
        // Step 1: tell YouTube what is coming and get an address to send it to. Nothing is sent yet, so a failure here is safe to retry.
        var body = JsonSerializer.Serialize(new
        {
            snippet = new { title = upload.Title, description = upload.Description, categoryId = "22" },
            status = new { privacyStatus = upload.Privacy, selfDeclaredMadeForKids = false, containsSyntheticMedia = upload.SyntheticMedia },
        });
        using var start = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        start.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/mp4");
        start.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString());
        using var started = await SendAsync(start, SocialFailure.Transient, ct);
        if (!started.IsSuccessStatusCode)
            throw Fail(started.StatusCode, await ErrorOf(started, ct), SocialFailure.Transient);

        var session = started.Headers.Location;
        if (session is null || session.Scheme != Uri.UriSchemeHttps || !session.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase))
            throw new SocialProviderException("YouTube did not give a valid place to send the video.", SocialFailure.Permanent);

        // Step 2: send the bytes. From here on a break in the connection is ambiguous - the video may already be there.
        using var send = new HttpRequestMessage(HttpMethod.Put, session) { Content = new StreamContent(video) };
        send.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        send.Content.Headers.ContentLength = length;
        send.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var sent = await SendAsync(send, SocialFailure.Ambiguous, ct);
        if (!sent.IsSuccessStatusCode)
        {
            var error = await ErrorOf(sent, ct);
            throw Fail(sent.StatusCode, error, SocialFailure.Ambiguous);
        }

        using var doc = await ParseAsync(sent, ct);
        return Text(doc.RootElement, "id") ?? throw new SocialProviderException("YouTube accepted the video but did not say its id. Look at your channel before trying again.", SocialFailure.Ambiguous);
    }

    public async Task<IReadOnlyDictionary<string, VideoStatistics>> GetVideoStatisticsAsync(string accessToken, IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, VideoStatistics>();
        var ids = videoIds.Where(id => id.Length is > 0 and <= 20 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')).Distinct().Take(50).ToList();
        if (ids.Count == 0)
            return result;

        using var request = new HttpRequestMessage(HttpMethod.Get, StatisticsEndpoint + Uri.EscapeDataString(string.Join(",", ids)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, SocialFailure.Transient, ct);
        if (!response.IsSuccessStatusCode)
            throw Fail(response.StatusCode, await ErrorOf(response, ct), SocialFailure.Transient);

        using var doc = await ParseAsync(response, ct);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id");
            if (id is null || !item.TryGetProperty("statistics", out var stats))
                continue;
            result[id] = new VideoStatistics(Count(stats, "viewCount"), Count(stats, "likeCount"), Count(stats, "commentCount"));
        }

        return result;
    }

    private static long Count(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var n) ? Math.Max(0, n) : 0;

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }) };
        using var response = await SendAsync(request, SocialFailure.Transient, ct);
        _ = response; // best effort: a token Google no longer knows is already revoked
    }

    // ----- plumbing -----------------------------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, SocialFailure onNetworkError, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "A request to Google failed before an answer came back.");
            throw new SocialProviderException(
                onNetworkError == SocialFailure.Ambiguous
                    ? "Contact was lost while the video was being sent, so it may or may not be on YouTube. Look at your channel before trying again."
                    : "Google could not be reached. This is usually temporary.",
                onNetworkError, ex);
        }
    }

    private sealed record GoogleError(string? Reason, string? Message);

    private static async Task<GoogleError> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return new GoogleError(error.GetString(), Text(root, "error_description"));
                var reason = error.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? Text(list[0], "reason") : null;
                return new GoogleError(reason ?? Text(error, "status"), Text(error, "message"));
            }
        }
        catch (JsonException)
        {
        }

        return new GoogleError(null, null);
    }

    /// <summary>What a Google error means for the tenant. <paramref name="unknown"/> is what an error we do not recognise is treated as.</summary>
    private static SocialProviderException Fail(HttpStatusCode status, GoogleError error, SocialFailure unknown)
    {
        var code = (int)status;
        switch (error.Reason)
        {
            case "quotaExceeded" or "dailyLimitExceeded" or "rateLimitExceeded" or "userRateLimitExceeded":
                return new SocialProviderException("YouTube's daily allowance for uploads from this platform is used up. Try again tomorrow.", SocialFailure.Permanent);
            case "uploadLimitExceeded":
                return new SocialProviderException("Your YouTube channel has reached its upload limit for now. Try again later.", SocialFailure.Permanent);
            case "youtubeSignupRequired" or "channelNotFound":
                return new SocialProviderException("That Google account needs a YouTube channel before it can publish.", SocialFailure.Permanent);
            case "forbidden" or "insufficientPermissions" or "authError" or "unauthorized":
                return new SocialProviderException("Your YouTube account did not allow this. Connect it again and allow every permission.", SocialFailure.NeedsReconnect);
            case "invalidTitle" or "invalidDescription" or "invalidVideoMetadata":
                return new SocialProviderException("YouTube did not accept the title or description. Change them and try again.", SocialFailure.Permanent);
            case "mediaBodyRequired" or "invalidVideo" or "unsupportedMediaType":
                return new SocialProviderException("YouTube could not use the video file.", SocialFailure.Permanent);
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new SocialProviderException("Google no longer accepts the saved sign-in. Connect your YouTube account again.", SocialFailure.NeedsReconnect),
            HttpStatusCode.TooManyRequests => new SocialProviderException("Google is limiting requests right now.", SocialFailure.Transient),
            _ when code >= 500 => new SocialProviderException("Google had a problem on its side.", unknown),
            _ => new SocialProviderException("YouTube did not accept this. " + (error.Message is { Length: > 0 } m ? "It said: " + Truncate(m, 200) : string.Empty), SocialFailure.Permanent),
        };
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new SocialProviderException("Google gave an answer that could not be read.", SocialFailure.Transient, ex);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
