using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.SocialAds;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.SocialAds;

/// <summary>
/// Talks to Meta's Graph / Marketing API with the <c>ads_read</c> permission only. Never exercised against a live Meta
/// App in this codebase - treat the first real login as the real test, and the first few syncs as the way to find any
/// field Meta names differently than documented.
///
/// The typed HttpClient has its loggers removed (see DependencyInjection): the code exchange puts the App Secret and
/// tokens in the query string, and the default HttpClient logging would write those URLs to the log files. Calls that
/// carry only an access token send it in the Authorization header instead. Error messages built here never include a
/// request URL, a token or a secret.
/// </summary>
public class MetaAdsClient : IMetaAdsClient
{
    private const int PageSize = 500;
    private const int MaxPages = 50;
    private const int MaxErrorLength = 300;

    /// <summary>Action types Meta reports for lead-generation results; counted together as "leads".</summary>
    private static readonly HashSet<string> LeadActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "lead",
        "onsite_conversion.lead_grouped",
        "offsite_conversion.fb_pixel_lead",
    };

    private readonly HttpClient _http;
    private readonly IOptionsSnapshot<MetaAdsOptions> _options;
    private readonly IOptionsSnapshot<WhatsAppSettings> _whatsApp;

    public MetaAdsClient(HttpClient http, IOptionsSnapshot<MetaAdsOptions> options, IOptionsSnapshot<WhatsAppSettings> whatsApp)
    {
        _http = http;
        _options = options;
        _whatsApp = whatsApp;
        // Fail fast into the next sync instead of hanging a worker on a dead connection.
        _http.Timeout = TimeSpan.FromSeconds(45);
    }

    private string AppId => FirstNonEmpty(_options.Value.AppId, _whatsApp.Value.AppId);

    private string AppSecret => FirstNonEmpty(_options.Value.AppSecret, _whatsApp.Value.AppSecret);

    public bool IsConfigured => AppId.Length > 0 && AppSecret.Length > 0;

    public string BuildLoginUrl(string redirectUri, string state) =>
        $"{WithSlash(_options.Value.DialogBaseUrl)}{_options.Value.ApiVersion}/dialog/oauth" +
        $"?client_id={Uri.EscapeDataString(AppId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        "&scope=ads_read&response_type=code";

    public async Task<MetaToken> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        var shortLived = await GetTokenAsync(
            "oauth/access_token" +
            $"?client_id={Uri.EscapeDataString(AppId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&client_secret={Uri.EscapeDataString(AppSecret)}" +
            $"&code={Uri.EscapeDataString(code)}",
            cancellationToken);

        return await RefreshTokenAsync(shortLived.AccessToken, cancellationToken);
    }

    public Task<MetaToken> RefreshTokenAsync(string accessToken, CancellationToken cancellationToken = default) =>
        GetTokenAsync(
            "oauth/access_token?grant_type=fb_exchange_token" +
            $"&client_id={Uri.EscapeDataString(AppId)}" +
            $"&client_secret={Uri.EscapeDataString(AppSecret)}" +
            $"&fb_exchange_token={Uri.EscapeDataString(accessToken)}",
            cancellationToken);

    public async Task<IReadOnlyList<MetaAdAccount>> ListAdAccountsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var accounts = new List<MetaAdAccount>();
        string? after = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var path = "me/adaccounts?fields=account_id,name,currency&limit=100" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
            using var doc = await GetJsonAsync(path, accessToken, cancellationToken);

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = GetString(item, "account_id");
                    if (string.IsNullOrEmpty(id))
                        continue;
                    accounts.Add(new MetaAdAccount(id, GetString(item, "name") ?? $"Ad account {id}", GetString(item, "currency") ?? string.Empty));
                }
            }

            after = NextCursor(doc.RootElement);
            if (after is null)
                break;
        }

        return accounts;
    }

    public async Task<IReadOnlyList<MetaInsightRow>> GetInsightsAsync(
        string accessToken, string adAccountId, DateTime since, DateTime until, CancellationToken cancellationToken = default)
    {
        var timeRange = $"{{\"since\":\"{since:yyyy-MM-dd}\",\"until\":\"{until:yyyy-MM-dd}\"}}";
        var rows = new List<MetaInsightRow>();
        string? after = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var path = $"act_{Uri.EscapeDataString(adAccountId)}/insights" +
                       "?level=account&time_increment=1&breakdowns=publisher_platform" +
                       "&fields=spend,impressions,clicks,actions" +
                       $"&time_range={Uri.EscapeDataString(timeRange)}&limit={PageSize}" +
                       (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");

            using var doc = await GetJsonAsync(path, accessToken, cancellationToken);
            rows.AddRange(ParseInsights(doc.RootElement));

            after = NextCursor(doc.RootElement);
            if (after is null)
                return rows;
        }

        throw new MetaApiException("Meta returned more ad data than could be read in one go. Try again later.");
    }

    /// <summary>Public for tests: turns one insights response page into rows. Rows with no parseable date are skipped.</summary>
    public static IReadOnlyList<MetaInsightRow> ParseInsights(JsonElement root)
    {
        var rows = new List<MetaInsightRow>();
        if (!root.TryGetProperty("data", out var data))
            return rows;

        foreach (var item in data.EnumerateArray())
        {
            if (!DateTime.TryParseExact(GetString(item, "date_start"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                continue;

            var leads = 0;
            if (item.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.EnumerateArray())
                {
                    var type = GetString(action, "action_type");
                    if (type is not null && LeadActionTypes.Contains(type) && int.TryParse(GetString(action, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                        leads += count;
                }
            }

            rows.Add(new MetaInsightRow(
                date,
                (GetString(item, "publisher_platform") ?? "unknown").ToLowerInvariant(),
                ParseDecimal(GetString(item, "spend")),
                ParseLong(GetString(item, "impressions")),
                ParseLong(GetString(item, "clicks")),
                leads));
        }

        return rows;
    }

    private async Task<MetaToken> GetTokenAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        // The secrets are in this request's URL, so it is sent without the Authorization header and never logged.
        using var doc = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(pathAndQuery)), cancellationToken);
        var token = GetString(doc.RootElement, "access_token");
        if (string.IsNullOrEmpty(token))
            throw new MetaApiException("Meta did not return an access token.");

        DateTime? expiresAt = doc.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var seconds) && seconds > 0
            ? DateTime.UtcNow.AddSeconds(seconds)
            : null;
        return new MetaToken(token, expiresAt);
    }

    private Task<JsonDocument> GetJsonAsync(string pathAndQuery, string accessToken, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url(pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            string body;
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Neither exception's message includes the request URL.
                throw new MetaApiException($"Could not reach Meta: {ex.Message}");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw ToException((int)response.StatusCode, body);
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new MetaApiException("Meta returned a response that could not be read.");
            }
        }
    }

    /// <summary>Code 190 (and the OAuth type) means the token is no longer valid; everything else is a transient or
    /// request problem that must not make the tenant reconnect.</summary>
    public static Exception ToException(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var parsed) ? parsed : 0;
                var message = Truncate(GetString(error, "message") ?? "no detail");

                // 190 = invalid/expired/revoked token, 102 = expired API session.
                if (code is 190 or 102)
                    return new MetaAuthException("Meta no longer accepts the Facebook login. Please connect again.");

                if (code == 100 && message.Contains("authorization code", StringComparison.OrdinalIgnoreCase))
                    return new MetaAuthException("That Facebook login could not be completed (the code was used or expired). Please try again.");

                return new MetaApiException($"Meta rejected the request ({statusCode}, code {code}): {message}");
            }
        }
        catch (JsonException)
        {
            // fall through to the generic message
        }

        return new MetaApiException($"Meta rejected the request ({statusCode}).");
    }

    private Uri Url(string pathAndQuery) => new($"{WithSlash(_options.Value.ApiBaseUrl)}{_options.Value.ApiVersion}/{pathAndQuery}");

    private static string? NextCursor(JsonElement root) =>
        root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("next", out _)
        && paging.TryGetProperty("cursors", out var cursors) ? GetString(cursors, "after") : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null
            : null;

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;

    private static string WithSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private static string FirstNonEmpty(string first, string second) => !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? string.Empty;

    private static string Truncate(string text) => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
}
